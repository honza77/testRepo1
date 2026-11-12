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

// ------------------------------------------------------------------ uninstall mode

let private uninstallServer (ctx: ConnectContext) (opts: Options) (msis: string list) (computer: string) : UninstallResult list =
    let row name msi (p: InstalledProduct option) status exitCode errorCode message logFile =
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
                              let logFile =
                                  sprintf @"%s\%s_%s.log" opts.RemoteLogFolder (p.ProductCode.Trim('{', '}')) (DateTime.Now.ToString "yyyyMMdd_HHmmss")
                              Report.log None (sprintf "[%s] Uninstalling %s %s %s ..." name p.DisplayName p.Version p.ProductCode)
                              let outcome =
                                  try
                                      // retry while another MSI installation is in progress (1618)
                                      let rec attempt n =
                                          let code = session.Uninstall(p.ProductCode, opts.RemoteLogFolder, logFile)
                                          if code = 1618 && n < 5 then
                                              Report.warn (sprintf "[%s] Another installation in progress (MSI1618), retry %d/4 in 30 s..." name n)
                                              Thread.Sleep(TimeSpan.FromSeconds 30.0)
                                              attempt (n + 1)
                                          else
                                              code
                                      Ok(attempt 1)
                                  with ex ->
                                      Result.Error(ErrorClassifier.classify "MRU405" ex)
                              match outcome with
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

// ------------------------------------------------------------------ main

let private run (opts: Options) : int =
    let servers = mergeList opts.Computers opts.ComputerListFiles
    let msis = mergeList opts.Msis opts.MsiListFiles
    if List.isEmpty servers then
        raiseMru "MRU102" "Use --computer and/or --computer-list."
    if List.isEmpty msis && not opts.ListInstalled then
        raiseMru "MRU103" "Use --msi and/or --msi-list, or --list to list installed MSIs."

    // Option combinations that have no effect
    if not (List.isEmpty opts.Filters) && not opts.ListInstalled then
        Report.warnCode "MRU108" "--filter is only used together with --list."
    if opts.Transport = Transport.Wmi && (opts.UseSsl || opts.Port.IsSome || opts.Auth <> AuthMode.Default || opts.SkipCertCheck) then
        Report.warnCode "MRU108" "--ssl / --port / --auth / --skip-cert-check apply to the psremoting transport only."
    if opts.Transport = Transport.PsRemoting && opts.SkipCertCheck && not opts.UseSsl then
        Report.warnCode "MRU108" "--skip-cert-check has no effect without --ssl."
    if opts.CurrentUser && (opts.UserName.IsSome || opts.PasswordEnvVar.IsSome) then
        Report.warnCode "MRU108" "--current-user given; --user / --password-env are ignored."

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
    if not opts.ListInstalled then
        for m in msis do
            if not (isProductCode m) then
                try
                    createMatcher FilterMode.Wildcard m |> ignore
                with ex ->
                    raiseMru "MRU107" (sprintf "'%s': %s" m ex.Message)

    if not (OperatingSystem.IsWindows()) then
        Report.warnCode "MRU503" "Not running on Windows - connections are expected to fail."

    let reportPath =
        match opts.ReportPath with
        | Some p -> p
        | None ->
            let prefix = if opts.ListInstalled then "MsiInventory" else "MsiUninstallReport"
            Path.Combine(Environment.CurrentDirectory, sprintf "%s_%s.csv" prefix (DateTime.Now.ToString "yyyyMMdd_HHmmss"))

    Console.WriteLine()
    Report.info (sprintf "Transport     : %s%s" (transportName opts.Transport) (if opts.Transport = Transport.PsRemoting && opts.UseSsl then " over HTTPS" else ""))
    Report.info (sprintf "Servers (%d)   : %s" servers.Length (String.Join(", ", servers)))
    if opts.ListInstalled then
        if List.isEmpty matchers then Report.info "Filter        : none (listing all MSI products)"
        else Report.info (sprintf "Filter (%A, any of): %s" opts.FilterMode (String.Join(", ", opts.Filters)))
    else
        Report.info (sprintf "MSIs (%d)      : %s" msis.Length (String.Join(", ", msis)))
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
    else
        let proceed =
            if opts.WhatIf then
                Report.warn "WhatIf mode: products will be discovered but NOT uninstalled."
                true
            elif opts.Yes then
                true
            else
                Console.Write(sprintf "Uninstall %d MSI(s) on %d server(s)? [y/N] " msis.Length servers.Length)
                let answer = Console.ReadLine()
                not (isNull answer) && (let a = answer.Trim().ToLowerInvariant() in a = "y" || a = "yes")
        if not proceed then
            raiseMru "MRU502" "Nothing was uninstalled."
        let results = servers |> runParallel opts.Throttle (uninstallServer ctx opts msis) |> List.concat
        Report.showUninstallResults results reportPath |> ignore
        let connectionFailures =
            results |> List.filter (fun r -> r.Status = ConnectionFailed) |> List.map (fun r -> r.ComputerName) |> List.distinct
        if connectionFailures.Length = servers.Length then ExitNoServerReachable
        elif results |> List.exists (fun r -> isFailure r.Status) then ExitPartialFailure
        elif results |> List.exists (fun r -> r.Status = SuccessRebootRequired || r.Status = SuccessRebootInitiated) then ExitOkRebootRequired
        else ExitOk

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
