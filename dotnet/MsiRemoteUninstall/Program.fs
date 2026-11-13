module MsiRemoteUninstall.Program

open System
open System.IO
open System.Threading
open MsiRemoteUninstall.Errors
open MsiRemoteUninstall.Domain
open MsiRemoteUninstall.Cli
open MsiRemoteUninstall.Transport

// ------------------------------------------------------------------ process exit codes

[<Literal>]
let ExitOk = 0

[<Literal>]
let ExitPartialFailure = 1

[<Literal>]
let ExitInvalidInput = 2

[<Literal>]
let ExitCredentials = 3

[<Literal>]
let ExitAborted = 4

[<Literal>]
let ExitNoServerReachable = 5

[<Literal>]
let ExitInternal = 6

[<Literal>]
let ExitOkRebootRequired = 10

/// Exit code for an error that stops the whole run.
let exitCodeForFatal (code: string) =
    match (Errors.get code).Category with
    | Category.Input -> ExitInvalidInput
    | Category.Credentials -> ExitCredentials
    | _ when code = "MRU502" -> ExitAborted
    | Category.Internal -> ExitInternal
    | _ -> ExitPartialFailure

/// Prints a fatal error with explanation and remediation.
let private reportFatal (code: string) (detail: string) =
    let e = Errors.get code
    Report.error (sprintf "[%s] %s%s" code e.Title (if String.IsNullOrWhiteSpace detail then "" else ": " + detail))
    Console.WriteLine()
    Console.WriteLine(Errors.explainText e)

// ------------------------------------------------------------------ helpers

/// Runs f for every item with at most `throttle` running at the same time.
let private runParallel (throttle: int) (f: string -> 'T) (items: string list) : 'T list =
    items
    |> List.map (fun x -> async { return f x })
    |> fun jobs -> Async.Parallel(jobs, throttle)
    |> Async.RunSynchronously
    |> List.ofArray

let private transportName =
    function
    | Transport.PsRemoting -> "PowerShell Remoting (WinRM)"
    | Transport.Wmi -> "WMI (DCOM)"

type private ConnectContext =
    { Connect: string -> IRemoteSession
      Preflight: bool
      Port: int }

/// Preflight check + open session. Error = (code, detail).
let private tryConnect (ctx: ConnectContext) (computer: string) : Result<IRemoteSession, string * string> =
    try
        if ctx.Preflight then
            Preflight.check computer ctx.Port (TimeSpan.FromSeconds 5.0)
        Ok(ctx.Connect computer)
    with ex ->
        Result.Error(ErrorClassifier.classify "MRU399" ex)

let private logCode (computer: string) (code: string) (detail: string) =
    let e = Errors.get code
    let text = sprintf "[%s] [%s] %s: %s" computer code e.Title detail
    match e.Severity with
    | Severity.Error -> Report.error text
    | Severity.Warning -> Report.warn text
    | Severity.Info -> Report.log None text

// ------------------------------------------------------------------ list mode

let private inventoryServer (ctx: ConnectContext) (matchers: (string -> bool) list) (computer: string) : InventoryRow list =
    Report.info (sprintf "[%s] Connecting..." computer)
    match tryConnect ctx computer with
    | Result.Error(code, detail) ->
        logCode computer code detail
        [ { ComputerName = computer; Product = None; Error = Some(code, detail) } ]
    | Ok session ->
        use session = session
        try
            let products = session.GetInstalledProducts() |> List.filter (productPassesFilters matchers)
            Report.info (sprintf "[%s] %d matching MSI product(s)." session.ComputerName products.Length)
            products |> List.map (fun p -> { ComputerName = session.ComputerName; Product = Some p; Error = None })
        with ex ->
            let code, detail = ErrorClassifier.classify "MRU401" ex
            logCode computer code detail
            [ { ComputerName = computer; Product = None; Error = Some(code, detail) } ]

// ------------------------------------------------------------------ shared for install / uninstall

let private resultRow name msi (p: InstalledProduct option) status exitCode errorCode message logFile : OperationResult =
    { ComputerName = name
      Msi = msi
      DisplayName = p |> Option.map (fun p -> p.DisplayName) |> Option.defaultValue ""
      Version = p |> Option.map (fun p -> p.Version) |> Option.defaultValue ""
      ProductCode = p |> Option.map (fun p -> p.ProductCode) |> Option.defaultValue ""
      Status = status
      ExitCode = exitCode
      ErrorCode = errorCode
      Message = message
      LogFile = logFile }

/// Runs msiexec, retrying while another installation is in progress (1618).
let private runMsiexecWithRetry (session: IRemoteSession) (name: string) (arguments: string) (logFolder: string) : Result<int, string * string> =
    try
        let rec attempt n =
            let code = session.RunMsiexec(arguments, logFolder)
            if code = 1618 && n < 5 then
                Report.warn (sprintf "[%s] Another installation in progress (MSI1618), retry %d/4 in 30 s..." name n)
                Thread.Sleep(TimeSpan.FromSeconds 30.0)
                attempt (n + 1)
            else
                code
        Ok(attempt 1)
    with ex ->
        Result.Error(ErrorClassifier.classify "MRU405" ex)

let private logFileName (folder: string) (id: string) =
    sprintf @"%s\%s_%s.log" folder (id.Trim('{', '}')) (DateTime.Now.ToString "yyyyMMdd_HHmmss_fff")

// ------------------------------------------------------------------ uninstall mode

let private uninstallServer (ctx: ConnectContext) (opts: Options) (props: (string * string) list) (msis: string list) (computer: string) : OperationResult list =
    let row = resultRow
    let allMsis = String.Join("; ", msis)

    Report.info (sprintf "[%s] Connecting..." computer)
    match tryConnect ctx computer with
    | Result.Error(code, detail) ->
        logCode computer code detail
        [ row computer allMsis None ConnectionFailed None code detail "" ]
    | Ok session ->
        use session = session
        let name = session.ComputerName
        let installed =
            try
                Ok(session.GetInstalledProducts())
            with ex ->
                Result.Error(ErrorClassifier.classify "MRU401" ex)
        match installed with
        | Result.Error(code, detail) ->
            logCode name code detail
            [ row name allMsis None ExecutionError None code detail "" ]
        | Ok installed ->
            let handled = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            [ for msi in msis do
                  match findProducts installed msi with
                  | [] ->
                      Report.log None (sprintf "[%s] '%s': not installed." name msi)
                      yield row name msi None NotFound None "" "No matching MSI product installed." ""
                  | found ->
                      for p in found do
                          if not (handled.Add p.ProductCode) then
                              yield row name msi (Some p) NotInstalled None "" "Already handled by an earlier MSI entry." ""
                          elif opts.WhatIf then
                              Report.log None (sprintf "[%s] WhatIf: would uninstall %s %s %s" name p.DisplayName p.Version p.ProductCode)
                              yield row name msi (Some p) WouldUninstall None "" "WhatIf: not uninstalled." ""
                          else
                              let logFile = logFileName opts.RemoteLogFolder p.ProductCode
                              Report.log None (sprintf "[%s] Uninstalling %s %s %s ..." name p.DisplayName p.Version p.ProductCode)
                              let arguments = msiexecArguments "/x" p.ProductCode logFile props
                              match runMsiexecWithRetry session name arguments opts.RemoteLogFolder with
                              | Ok exitCode ->
                                  let status, msg, errorCode = interpretExitCode exitCode
                                  if isFailure status then
                                      logCode name errorCode (sprintf "%s - %s" p.DisplayName msg)
                                  else
                                      Report.ok (sprintf "[%s] %s: %s" name p.DisplayName msg)
                                  yield row name msi (Some p) status (Some exitCode) errorCode msg logFile
                              | Result.Error(code, detail) ->
                                  logCode name code (sprintf "%s - %s" p.DisplayName detail)
                                  yield row name msi (Some p) ExecutionError None code detail logFile ]

// ------------------------------------------------------------------ install mode

let private installServer (ctx: ConnectContext) (opts: Options) (props: (string * string) list) (packages: Packages.ResolvedPackage list) (computer: string) : OperationResult list =
    let pkgProduct (pkg: Packages.ResolvedPackage) : InstalledProduct option =
        pkg.Info
        |> Option.map (fun i ->
            { ProductCode = i.ProductCode
              DisplayName = i.ProductName
              Version = i.ProductVersion
              Publisher = i.Manufacturer
              InstallDate = ""
              Architecture = "" })
    let allPackages = packages |> List.map (fun p -> p.FileName) |> fun l -> String.Join("; ", l)

    Report.info (sprintf "[%s] Connecting..." computer)
    match tryConnect ctx computer with
    | Result.Error(code, detail) ->
        logCode computer code detail
        [ resultRow computer allPackages None ConnectionFailed None code detail "" ]
    | Ok session ->
        use session = session
        let name = session.ComputerName
        // installed product codes, to skip packages that are already installed
        let installedCodes =
            try
                session.GetInstalledProducts() |> List.map (fun p -> p.ProductCode) |> Set.ofList
            with ex ->
                let code, detail = ErrorClassifier.classify "MRU401" ex
                logCode name code (detail + " - continuing without the already-installed check")
                Set.empty
        [ for pkg in packages do
              let product = pkgProduct pkg
              let code = pkg.Info |> Option.map (fun i -> i.ProductCode) |> Option.defaultValue ""
              if code <> "" && installedCodes.Contains code then
                  Report.log None (sprintf "[%s] %s is already installed (%s) - skipped." name pkg.FileName code)
                  yield resultRow name pkg.FileName product AlreadyInstalled None "" "Already installed - skipped." ""
              elif opts.WhatIf then
                  Report.log None (sprintf "[%s] WhatIf: would install %s" name pkg.FileName)
                  yield resultRow name pkg.FileName product WouldInstall None "" "WhatIf: not installed." ""
              else
                  // 1. stage the package on the server (copy mode)
                  let staged =
                      if pkg.Copy then
                          let remotePath = sprintf @"%s\%s" opts.RemoteStagingFolder pkg.FileName
                          Report.log None (sprintf "[%s] Copying %s to %s ..." name pkg.FileName remotePath)
                          try
                              session.UploadFile(pkg.Path, remotePath)
                              Ok remotePath
                          with ex ->
                              Result.Error(ErrorClassifier.classify "MRU406" ex)
                      else
                          Ok pkg.Path
                  match staged with
                  | Result.Error(errCode, detail) ->
                      logCode name errCode (sprintf "%s - %s" pkg.FileName detail)
                      yield resultRow name pkg.FileName product ExecutionError None errCode detail ""
                  | Ok target ->
                      // 2. install
                      let logFile = logFileName opts.RemoteLogFolder ("install_" + Path.GetFileNameWithoutExtension pkg.FileName)
                      Report.log None (sprintf "[%s] Installing %s ..." name pkg.FileName)
                      let arguments = msiexecArguments "/i" target logFile props
                      let outcome = runMsiexecWithRetry session name arguments opts.RemoteLogFolder
                      // 3. remove the staged copy
                      if pkg.Copy && not opts.KeepStaged then
                          try
                              session.DeleteFile target
                          with ex ->
                              let c, d = ErrorClassifier.classify "MRU407" ex
                              logCode name c d
                      match outcome with
                      | Ok exitCode ->
                          let status, msg, errorCode = interpretInstallExitCode exitCode
                          if isFailure status then
                              logCode name errorCode (sprintf "%s - %s" pkg.FileName msg)
                          else
                              Report.ok (sprintf "[%s] %s: %s" name pkg.FileName msg)
                          yield resultRow name pkg.FileName product status (Some exitCode) errorCode msg logFile
                      | Result.Error(errCode, detail) ->
                          logCode name errCode (sprintf "%s - %s" pkg.FileName detail)
                          yield resultRow name pkg.FileName product ExecutionError None errCode detail logFile ]

// ------------------------------------------------------------------ main

let private exitCodeFor (servers: string list) (results: OperationResult list) =
    let connectionFailures =
        results |> List.filter (fun r -> r.Status = ConnectionFailed) |> List.map (fun r -> r.ComputerName) |> List.distinct
    if connectionFailures.Length = servers.Length then ExitNoServerReachable
    elif results |> List.exists (fun r -> isFailure r.Status) then ExitPartialFailure
    elif results |> List.exists (fun r -> r.Status = SuccessRebootRequired || r.Status = SuccessRebootInitiated) then ExitOkRebootRequired
    else ExitOk

let private confirm (opts: Options) (question: string) =
    if opts.WhatIf then
        Report.warn "WhatIf mode: nothing will be changed on the servers."
    elif not opts.Yes then
        Console.Write(question + " [y/N] ")
        let answer = Console.ReadLine()
        let yes = not (isNull answer) && (let a = answer.Trim().ToLowerInvariant() in a = "y" || a = "yes")
        if not yes then raiseMru "MRU502" "Nothing was changed."

let private run (opts: Options) : int =
    let servers = mergeList opts.Computers opts.ComputerListFiles
    let msis = mergeList opts.Msis opts.MsiListFiles
    let installEntries = mergeList opts.InstallPackages opts.InstallListFiles
    let installMode = not (List.isEmpty installEntries)

    // exactly one operation per run
    let modes = [ not (List.isEmpty msis); opts.ListInstalled; installMode ] |> List.filter id
    if modes.Length > 1 then
        raiseMru "MRU109" "Use only one of --msi/--msi-list, --list, --install/--install-list."
    if List.isEmpty servers then
        raiseMru "MRU102" "Use --computer and/or --computer-list."
    if modes.IsEmpty then
        raiseMru "MRU103" "Use --msi and/or --msi-list, --list to list installed MSIs, or --install to install packages."

    // Option combinations that have no effect
    if not (List.isEmpty opts.Filters) && not opts.ListInstalled then
        Report.warnCode "MRU108" "--filter is only used together with --list."
    if opts.Transport = Transport.Wmi && (opts.UseSsl || opts.Port.IsSome || opts.Auth <> AuthMode.Default || opts.SkipCertCheck) then
        Report.warnCode "MRU108" "--ssl / --port / --auth / --skip-cert-check apply to the psremoting transport only."
    if opts.Transport = Transport.PsRemoting && opts.SkipCertCheck && not opts.UseSsl then
        Report.warnCode "MRU108" "--skip-cert-check has no effect without --ssl."
    if opts.CurrentUser && (opts.UserName.IsSome || opts.PasswordEnvVar.IsSome) then
        Report.warnCode "MRU108" "--current-user given; --user / --password-env are ignored."
    if not installMode && (opts.PackageSource.IsSome || opts.SourceMode <> Packages.SourceMode.Copy || opts.KeepStaged) then
        Report.warnCode "MRU108" "--package-source / --source-mode / --keep-staged are only used with --install."
    if opts.ListInstalled && not (List.isEmpty opts.MsiProperties) then
        Report.warnCode "MRU108" "--msi-property is not used with --list."

    // msiexec properties (strict = command line passes through cmd.exe with WMI)
    let props = opts.MsiProperties |> List.map (parseMsiProperty (opts.Transport = Transport.Wmi))

    // Validate search and name patterns before contacting anything
    let matchers =
        if opts.ListInstalled then
            opts.Filters
            |> List.map (fun s -> s.Trim())
            |> List.filter ((<>) "")
            |> List.map (fun f ->
                try
                    createMatcher opts.FilterMode f
                with ex ->
                    raiseMru "MRU106" (sprintf "%A filter '%s': %s" opts.FilterMode f ex.Message))
        else
            []
    for m in msis do
        if not (isProductCode m) then
            try
                createMatcher FilterMode.Wildcard m |> ignore
            with ex ->
                raiseMru "MRU107" (sprintf "'%s': %s" m ex.Message)

    if not (OperatingSystem.IsWindows()) then
        Report.warnCode "MRU503" "Not running on Windows - connections are expected to fail."

    // Resolve install packages before asking for credentials (fails fast on typos)
    let packages =
        if installMode then
            let sourceMode =
                if opts.SourceMode = Packages.SourceMode.Direct && opts.PackageSource |> Option.exists Packages.isUrl then
                    Report.warnCode "MRU108" "--source-mode direct cannot be used with a URL source; packages are downloaded and copied."
                    Packages.SourceMode.Copy
                else
                    opts.SourceMode
            let downloadDir = Path.Combine(Path.GetTempPath(), "MsiRemoteUninstall", Guid.NewGuid().ToString("N"))
            let resolved = Packages.resolve opts.PackageSource sourceMode downloadDir installEntries
            for p in resolved do
                match p.InfoError with
                | Some e when p.Copy || Packages.isUnc p.Path -> Report.warnCode "MRU504" (sprintf "%s: %s" p.FileName e)
                | _ -> ()
            resolved
        else
            []

    let reportPath =
        match opts.ReportPath with
        | Some p -> p
        | None ->
            let prefix =
                if opts.ListInstalled then "MsiInventory"
                elif installMode then "MsiInstallReport"
                else "MsiUninstallReport"
            Path.Combine(Environment.CurrentDirectory, sprintf "%s_%s.csv" prefix (DateTime.Now.ToString "yyyyMMdd_HHmmss"))

    Console.WriteLine()
    Report.info (sprintf "Transport     : %s%s" (transportName opts.Transport) (if opts.Transport = Transport.PsRemoting && opts.UseSsl then " over HTTPS" else ""))
    Report.info (sprintf "Servers (%d)   : %s" servers.Length (String.Join(", ", servers)))
    if opts.ListInstalled then
        if List.isEmpty matchers then Report.info "Filter        : none (listing all MSI products)"
        else Report.info (sprintf "Filter (%A, any of): %s" opts.FilterMode (String.Join(", ", opts.Filters)))
    elif installMode then
        Report.info (sprintf "Packages (%d)  :" packages.Length)
        for p in packages do
            let details =
                match p.Info with
                | Some i -> sprintf "%s %s %s" i.ProductName i.ProductVersion i.ProductCode
                | None -> ""
            Report.info (sprintf "  %s  %s%s" p.Path details (if p.Copy then "" else "  (opened by the server)"))
    else
        Report.info (sprintf "MSIs (%d)      : %s" msis.Length (String.Join(", ", msis)))
    if not (List.isEmpty props) then
        Report.info (sprintf "MSI properties: %s" (formatMsiProperties props))
    Console.WriteLine()

    let credential = Credentials.obtain opts.UserName opts.PasswordEnvVar opts.CurrentUser

    let settings =
        { Credential = credential
          UseSsl = opts.UseSsl
          SkipCertCheck = opts.SkipCertCheck
          Port = opts.Port
          Auth = opts.Auth
          Timeout = TimeSpan.FromMinutes(float opts.TimeoutMinutes) }

    let ctx =
        { Connect =
            (match opts.Transport with
             | Transport.PsRemoting -> PsRemotingTransport.connect settings
             | Transport.Wmi -> WmiTransport.connect settings)
          Preflight = not opts.SkipPreflight
          Port = Preflight.managementPort opts.Transport opts.UseSsl opts.Port }

    if opts.ListInstalled then
        let rows = servers |> runParallel opts.Throttle (inventoryServer ctx matchers) |> List.concat
        Report.showInventory rows reportPath |> ignore
        let failedServers = rows |> List.filter (fun r -> r.Error.IsSome) |> List.length
        if failedServers = 0 then ExitOk
        elif failedServers = servers.Length then ExitNoServerReachable
        else ExitPartialFailure
    elif installMode then
        confirm opts (sprintf "Install %d package(s) on %d server(s)?" packages.Length servers.Length)
        let results = servers |> runParallel opts.Throttle (installServer ctx opts props packages) |> List.concat
        Report.showOperationResults results reportPath |> ignore
        exitCodeFor servers results
    else
        confirm opts (sprintf "Uninstall %d MSI(s) on %d server(s)?" msis.Length servers.Length)
        let results = servers |> runParallel opts.Throttle (uninstallServer ctx opts props msis) |> List.concat
        Report.showOperationResults results reportPath |> ignore
        exitCodeFor servers results

let private showErrors (format: string) =
    match format with
    | "markdown"
    | "md" -> Console.Write(Errors.markdown ())
    | "json" -> Console.WriteLine(Errors.json ())
    | _ -> Console.Write(Errors.listText ())

[<EntryPoint>]
let main argv =
    try
        let opts = parse argv
        if opts.ShowHelp || argv.Length = 0 then
            Console.WriteLine usage
            ExitOk
        else
            match opts.ListErrors, opts.Explain with
            | Some fmt, _ ->
                showErrors fmt
                ExitOk
            | None, Some code ->
                match Errors.tryFind code with
                | Some e ->
                    Console.WriteLine(Errors.explainText e)
                    ExitOk
                | None ->
                    Report.error (sprintf "Unknown error code '%s'. Use --errors to list all codes." code)
                    ExitInvalidInput
            | None, None -> run opts
    with
    | MruException(code, detail) ->
        if code = "MRU502" then
            Report.warn (sprintf "[%s] %s: %s" code (Errors.get code).Title detail)
        else
            reportFatal code detail
        exitCodeForFatal code
    | ex ->
        let code, detail = ErrorClassifier.classify "MRU999" ex
        reportFatal code detail
        if code = "MRU999" then ExitInternal else exitCodeForFatal code
