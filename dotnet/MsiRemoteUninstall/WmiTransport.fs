/// Transport 2: WMI over DCOM via System.Management - the "native" .NET way to
/// manage remote Windows machines without PowerShell or WinRM on the server.
///
///  * Inventory: StdRegProv (root\default) reads the registry Uninstall keys.
///  * Uninstall: Win32_Process.Create starts
///        cmd /v:on /c "start /wait msiexec /x {code} ... & reg add HKLM\...\<id> /d !errorlevel!"
///    Win32_Process.Create cannot return a child's exit code, so the command
///    writes msiexec's exit code to a temporary registry value which is read
///    back via StdRegProv once the process has ended, then deleted.
module MsiRemoteUninstall.WmiTransport

open System
open System.Management
open System.Threading
open MsiRemoteUninstall.Domain
open MsiRemoteUninstall.Transport
open MsiRemoteUninstall.Errors

let private HKLM = 0x80000002u
let private statusKey = @"SOFTWARE\MsiRemoteUninstall\ExitCodes"

let private isLocal (computer: string) =
    let c = computer.Trim().ToLowerInvariant()
    c = "." || c = "localhost" || c = "127.0.0.1" || c = "::1"
    || String.Equals(c.Split('.').[0], Environment.MachineName, StringComparison.OrdinalIgnoreCase)

let private u32 (o: obj) = if isNull o then UInt32.MaxValue else Convert.ToUInt32 o

type WmiSession(computer: string, settings: ConnectionSettings) =
    let options =
        let o = ConnectionOptions()
        o.Impersonation <- ImpersonationLevel.Impersonate
        o.Authentication <- AuthenticationLevel.PacketPrivacy
        o.EnablePrivileges <- true
        o.Timeout <- TimeSpan.FromMinutes 2.0
        match settings.Credential with
        // WMI refuses explicit credentials for local connections
        | Some c when not (isLocal computer) ->
            o.Username <- c.UserName
            o.SecurePassword <- c.Password
        | _ -> ()
        o

    let cimv2 = ManagementScope(sprintf @"\\%s\root\cimv2" computer, options)
    let defaultNs = ManagementScope(sprintf @"\\%s\root\default" computer, options)
    do
        cimv2.Connect()
        defaultNs.Connect()

    let regProv = new ManagementClass(defaultNs, ManagementPath("StdRegProv"), null)
    let processClass = new ManagementClass(cimv2, ManagementPath("Win32_Process"), null)

    let reg (methodName: string) (args: (string * obj) list) : ManagementBaseObject =
        use inParams = regProv.GetMethodParameters methodName
        for (n, v) in args do
            inParams.[n] <- v
        regProv.InvokeMethod(methodName, inParams, null)

    let enumKeys (path: string) : string[] =
        use out = reg "EnumKey" [ "hDefKey", box HKLM; "sSubKeyName", box path ]
        match u32 out.["ReturnValue"], out.["sNames"] with
        | 0u, (:? (string[]) as names) -> names
        | _ -> [||]

    let getString (path: string) (name: string) : string =
        use out = reg "GetStringValue" [ "hDefKey", box HKLM; "sSubKeyName", box path; "sValueName", box name ]
        match u32 out.["ReturnValue"], out.["sValue"] with
        | 0u, (:? string as s) -> s
        | _ -> ""

    let deleteValue (path: string) (name: string) =
        use _ = reg "DeleteValue" [ "hDefKey", box HKLM; "sSubKeyName", box path; "sValueName", box name ]
        ()

    let isProcessRunning (pid: uint32) =
        use searcher =
            new ManagementObjectSearcher(cimv2, ObjectQuery(sprintf "SELECT ProcessId FROM Win32_Process WHERE ProcessId = %d" pid))
        use results = searcher.Get()
        results.Count > 0

    let remoteName =
        try
            use searcher = new ManagementObjectSearcher(cimv2, ObjectQuery("SELECT CSName FROM Win32_OperatingSystem"))
            use results = searcher.Get()
            results
            |> Seq.cast<ManagementBaseObject>
            |> Seq.tryHead
            |> Option.map (fun o -> string o.["CSName"])
            |> Option.defaultValue computer
        with _ -> computer

    interface IRemoteSession with
        member _.ComputerName = remoteName

        member _.GetInstalledProducts() =
            uninstallKeys
            |> Seq.collect (fun (root, arch) ->
                enumKeys root
                |> Seq.filter isProductCode
                |> Seq.choose (fun key ->
                    let path = root + "\\" + key
                    match getString path "DisplayName" with
                    | "" -> None
                    | name ->
                        Some
                            { ProductCode = key.ToUpperInvariant()
                              DisplayName = name
                              Version = getString path "DisplayVersion"
                              Publisher = getString path "Publisher"
                              InstallDate = getString path "InstallDate"
                              Architecture = arch }))
            |> distinctProducts

        member _.Uninstall(productCode, logFolder, logFile) =
            // unique registry value name for this run
            let valueName = sprintf "%s_%s" productCode (Guid.NewGuid().ToString("N"))
            let commandLine =
                String.concat
                    ""
                    [ "cmd.exe /v:on /c \""
                      sprintf "(if not exist \"%s\" mkdir \"%s\")" logFolder logFolder
                      sprintf " & start \"\" /wait \"%%SystemRoot%%\\System32\\msiexec.exe\" /x %s /qn /norestart /l*v \"%s\"" productCode logFile
                      sprintf " & reg add \"HKLM\\%s\" /v \"%s\" /t REG_SZ /d !errorlevel! /f" statusKey valueName
                      "\"" ]

            let pid =
                use inParams = processClass.GetMethodParameters "Create"
                inParams.["CommandLine"] <- commandLine
                use out = processClass.InvokeMethod("Create", inParams, null)
                match u32 out.["ReturnValue"] with
                | 0u -> u32 out.["ProcessId"]
                | rv ->
                    let reason =
                        match rv with
                        | 2u -> "access denied"
                        | 3u -> "insufficient privilege"
                        | 8u -> "unknown failure"
                        | 9u -> "path not found"
                        | 21u -> "invalid parameter"
                        | _ -> "unexpected return value"
                    raiseMru "MRU402" (sprintf "Win32_Process.Create returned %d (%s)." rv reason)

            let deadline = DateTime.UtcNow + settings.Timeout
            while isProcessRunning pid && DateTime.UtcNow < deadline do
                Thread.Sleep(TimeSpan.FromSeconds 5.0)
            if isProcessRunning pid then
                raiseMru "MRU403" (sprintf "msiexec (cmd.exe PID %d) still running after %.0f minute(s); it was left running." pid settings.Timeout.TotalMinutes)

            let value = getString statusKey valueName
            (try deleteValue statusKey valueName with _ -> ())
            match Int32.TryParse value with
            | true, code -> code
            | _ -> raiseMru "MRU404" (sprintf "No exit code found in HKLM\\%s\\%s." statusKey valueName)

        member _.Dispose() =
            regProv.Dispose()
            processClass.Dispose()

let connect (settings: ConnectionSettings) (computer: string) : IRemoteSession =
    new WmiSession(computer, settings) :> IRemoteSession
