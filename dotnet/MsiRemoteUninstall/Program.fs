module MsiRemoteUninstall.Program

open System
open System.IO
open System.Threading
open MsiRemoteUninstall.Domain
open MsiRemoteUninstall.Cli
open MsiRemoteUninstall.Transport

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

/// Opens a session, turning any failure into an Error with a readable message.
let private tryConnect (connect: string -> IRemoteSession) (computer: string) =
    try
        Ok(connect computer)
    with ex ->
        let msg =
            match ex with
            | :? AggregateException as ae when not (isNull ae.InnerException) -> ae.InnerException.Message
            | _ -> ex.Message
        Error msg

// ------------------------------------------------------------------ list mode

let private inventoryServer connect (matchers: (string -> bool) list) (computer: string) : InventoryRow list =
    Report.info (sprintf "[%s] Connecting..." computer)
    match tryConnect connect computer with
    | Error msg ->
        Report.error (sprintf "[%s] %s" computer msg)
        [ { ComputerName = computer; Product = None; Error = Some msg } ]
    | Ok session ->
        use session = session
        try
            let products = session.GetInstalledProducts() |> List.filter (productPassesFilters matchers)
            Report.info (sprintf "[%s] %d matching MSI product(s)." session.ComputerName products.Length)
            products |> List.map (fun p -> { ComputerName = session.ComputerName; Product = Some p; Error = None })
        with ex ->
            Report.error (sprintf "[%s] %s" computer ex.Message)
            [ { ComputerName = computer; Product = None; Error = Some ex.Message } ]

// ------------------------------------------------------------------ uninstall mode

let private uninstallServer connect (opts: Options) (msis: string list) (computer: string) : UninstallResult list =
    let row name msi (p: InstalledProduct option) status exitCode message logFile =
        { ComputerName = name
          Msi = msi
          DisplayName = p |> Option.map (fun p -> p.DisplayName) |> Option.defaultValue ""
          Version = p |> Option.map (fun p -> p.Version) |> Option.defaultValue ""
          ProductCode = p |> Option.map (fun p -> p.ProductCode) |> Option.defaultValue ""
          Status = status
          ExitCode = exitCode
          Message = message
          LogFile = logFile }

    Report.info (sprintf "[%s] Connecting..." computer)
    match tryConnect connect computer with
    | Error msg ->
        Report.error (sprintf "[%s] %s" computer msg)
        [ row computer (String.Join("; ", msis)) None ConnectionFailed None msg "" ]
    | Ok session ->
        use session = session
        let name = session.ComputerName
        let installed =
            try
                Ok(session.GetInstalledProducts())
            with ex ->
                Error ex.Message
        match installed with
        | Error msg ->
            Report.error (sprintf "[%s] Could not read installed products: %s" name msg)
            [ row name (String.Join("; ", msis)) None ExecutionError None msg "" ]
        | Ok installed ->
            let handled = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            [ for msi in msis do
                  match findProducts installed msi with
                  | [] ->
                      Report.log None (sprintf "[%s] '%s': not installed." name msi)
                      yield row name msi None NotFound None "No matching MSI product installed." ""
                  | found ->
                      for p in found do
                          if not (handled.Add p.ProductCode) then
                              yield row name msi (Some p) NotInstalled None "Already handled by an earlier MSI entry." ""
                          elif opts.WhatIf then
                              Report.log None (sprintf "[%s] WhatIf: would uninstall %s %s %s" name p.DisplayName p.Version p.ProductCode)
                              yield row name msi (Some p) WouldUninstall None "WhatIf: not uninstalled." ""
                          else
                              let logFile =
                                  sprintf @"%s\%s_%s.log" opts.RemoteLogFolder (p.ProductCode.Trim('{', '}')) (DateTime.Now.ToString "yyyyMMdd_HHmmss")
                              Report.log None (sprintf "[%s] Uninstalling %s %s %s ..." name p.DisplayName p.Version p.ProductCode)
                              try
                                  // retry while another MSI installation is in progress (1618)
                                  let rec attempt n =
                                      let code = session.Uninstall(p.ProductCode, opts.RemoteLogFolder, logFile)
                                      if code = 1618 && n < 5 then
                                          Report.warn (sprintf "[%s] Another installation in progress, retry %d/4 in 30 s..." name n)
                                          Thread.Sleep(TimeSpan.FromSeconds 30.0)
                                          attempt (n + 1)
                                      else
                                          code
                                  let code = attempt 1
                                  let status, msg = interpretExitCode code
                                  (if isFailure status then Report.error else Report.ok) (sprintf "[%s] %s: %s" name p.DisplayName msg)
                                  yield row name msi (Some p) status (Some code) msg logFile
                              with ex ->
                                  Report.error (sprintf "[%s] %s: %s" name p.DisplayName ex.Message)
                                  yield row name msi (Some p) ExecutionError None ex.Message logFile ]

// ------------------------------------------------------------------ main

let private run (opts: Options) : int =
    let fail msg = raise (CliError msg)

    let servers = mergeList opts.Computers opts.ComputerListFiles
    let msis = mergeList opts.Msis opts.MsiListFiles
    if List.isEmpty servers then fail "No servers specified. Use --computer and/or --computer-list."
    if List.isEmpty msis && not opts.ListInstalled then
        fail "No MSIs specified. Use --msi and/or --msi-list (or --list to list installed MSIs)."
    if not (List.isEmpty opts.Filters) && not opts.ListInstalled then
        Report.warn "--filter is only used together with --list; ignoring it."
    if opts.Transport = Transport.Wmi && (opts.UseSsl || opts.Port.IsSome || opts.Auth <> AuthMode.Default || opts.SkipCertCheck) then
        Report.warn "--ssl / --port / --auth / --skip-cert-check apply to the psremoting transport only; ignoring them."
    if opts.SkipCertCheck && not opts.UseSsl then
        Report.warn "--skip-cert-check has no effect without --ssl."
    if opts.CurrentUser && (opts.UserName.IsSome || opts.PasswordEnvVar.IsSome) then
        Report.warn "--current-user given; --user / --password-env are ignored."

    // Validate search patterns before connecting anywhere
    let matchers =
        if opts.ListInstalled then
            opts.Filters
            |> List.map (fun s -> s.Trim())
            |> List.filter ((<>) "")
            |> List.map (fun f ->
                try
                    createMatcher opts.FilterMode f
                with ex ->
                    fail (sprintf "Invalid %A filter '%s': %s" opts.FilterMode f ex.Message))
        else
            []
    // Validate MSI name wildcards too
    for m in msis do
        if not (isProductCode m) then
            try
                createMatcher FilterMode.Wildcard m |> ignore
            with ex ->
                fail (sprintf "Invalid MSI name pattern '%s': %s" m ex.Message)

    if not (OperatingSystem.IsWindows()) then
        Report.warn "This tool uses Windows remote management APIs (WinRM client / DCOM); it is expected to run on Windows."

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

    let connect =
        match opts.Transport with
        | Transport.PsRemoting -> PsRemotingTransport.connect settings
        | Transport.Wmi -> WmiTransport.connect settings

    if opts.ListInstalled then
        let rows = servers |> runParallel opts.Throttle (inventoryServer connect matchers) |> List.concat
        Report.showInventory rows reportPath
        if rows |> List.exists (fun r -> r.Error.IsSome) then 1 else 0
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
                not (isNull answer) && (answer.Trim().ToLowerInvariant() = "y" || answer.Trim().ToLowerInvariant() = "yes")
        if not proceed then
            Report.warn "Aborted by user."
            1
        else
            let results = servers |> runParallel opts.Throttle (uninstallServer connect opts msis) |> List.concat
            Report.showUninstallResults results reportPath
            if results |> List.exists (fun r -> isFailure r.Status) then 1 else 0

[<EntryPoint>]
let main argv =
    try
        let opts = parse argv
        if opts.ShowHelp || argv.Length = 0 then
            Console.WriteLine usage
            0
        else
            run opts
    with
    | CliError msg ->
        Report.error msg
        2
    | ex ->
        Report.error ex.Message
        1
