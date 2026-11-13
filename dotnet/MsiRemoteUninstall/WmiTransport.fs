/// Transport 2: WMI over DCOM via System.Management - the "native" .NET way to
/// manage remote Windows machines without PowerShell or WinRM on the server.
///
///  * Inventory: StdRegProv (root\default) reads the registry Uninstall keys.
///  * msiexec: Win32_Process.Create starts
///        cmd /v:on /c "start /wait msiexec <args> & reg add HKLM\...\<id> /d !errorlevel!"
///    Win32_Process.Create cannot return a child's exit code, so the command
///    writes msiexec's exit code to a temporary registry value which is read
///    back via StdRegProv once the process has ended, then deleted.
///  * File upload (install mode): copied through the administrative share
///    \\server\C$ (SMB, port 445); explicit credentials are applied with
///    WNetAddConnection2.
module MsiRemoteUninstall.WmiTransport

open System
open System.ComponentModel
open System.IO
open System.Management
open System.Runtime.InteropServices
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

// ------------------------------------------------------------------ SMB (admin share)

[<StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
[<AllowNullLiteral>]
type private NetResource =
    class
        [<Microsoft.FSharp.Core.DefaultValue>]
        val mutable Scope: int
        [<Microsoft.FSharp.Core.DefaultValue>]
        val mutable ResourceType: int
        [<Microsoft.FSharp.Core.DefaultValue>]
        val mutable DisplayType: int
        [<Microsoft.FSharp.Core.DefaultValue>]
        val mutable Usage: int
        [<Microsoft.FSharp.Core.DefaultValue>]
        val mutable LocalName: string
        [<Microsoft.FSharp.Core.DefaultValue>]
        val mutable RemoteName: string
        [<Microsoft.FSharp.Core.DefaultValue>]
        val mutable Comment: string
        [<Microsoft.FSharp.Core.DefaultValue>]
        val mutable Provider: string
        new() = { }
    end

module private Mpr =
    [<DllImport("mpr.dll", CharSet = CharSet.Unicode)>]
    extern int WNetAddConnection2(NetResource netResource, string password, string username, int flags)

    [<DllImport("mpr.dll", CharSet = CharSet.Unicode)>]
    extern int WNetCancelConnection2(string name, int flags, bool force)

let private plainText (s: Security.SecureString) =
    let ptr = Marshal.SecureStringToGlobalAllocUnicode s
    try
        Marshal.PtrToStringUni ptr
    finally
        Marshal.ZeroFreeGlobalAllocUnicode ptr

/// Converts C:\dir\file to \\computer\C$\dir\file.
let adminSharePath (computer: string) (remotePath: string) =
    if remotePath.Length < 3 || remotePath.[1] <> ':' || remotePath.[2] <> '\\' || not (Char.IsLetter remotePath.[0]) then
        raiseMru "MRU406" (sprintf "'%s' is not an absolute local path (like C:\\Folder\\file.msi)." remotePath)
    sprintf @"\\%s\%c$\%s" computer (Char.ToUpperInvariant remotePath.[0]) (remotePath.Substring 3)

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

    // SMB connections opened with explicit credentials (closed on Dispose)
    let smbConnections = Collections.Generic.List<string>()

    let ensureShare (uncPath: string) =
        match settings.Credential with
        | Some c when not (isLocal computer) ->
            let share = uncPath.Substring(0, uncPath.IndexOf('$') + 1) // \\server\C$
            if not (smbConnections.Contains share) then
                let res = NetResource(ResourceType = 1 (* RESOURCETYPE_DISK *), RemoteName = share)
                let rc = Mpr.WNetAddConnection2(res, plainText c.Password, c.UserName, 0)
                if rc <> 0 then
                    raiseMru "MRU406" (sprintf "Connecting to %s failed: Windows error %d (%s)." share rc (Win32Exception(rc).Message))
                smbConnections.Add share
        | _ -> () // current logon is used

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

        member _.RunMsiexec(arguments, logFolder) =
            // unique registry value name for this run
            let valueName = Guid.NewGuid().ToString("N")
            let commandLine =
                String.concat
                    ""
                    [ "cmd.exe /v:on /c \""
                      sprintf "(if not exist \"%s\" mkdir \"%s\")" logFolder logFolder
                      sprintf " & start \"\" /wait \"%%SystemRoot%%\\System32\\msiexec.exe\" %s" arguments
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

        member _.UploadFile(localPath, remotePath) =
            let target = if isLocal computer then remotePath else adminSharePath computer remotePath
            try
                if not (isLocal computer) then ensureShare target
                Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                File.Copy(localPath, target, true)
            with
            | MruException _ -> reraise ()
            | ex -> raiseMru "MRU406" (sprintf "Copy to %s failed: %s" target (firstLine ex.Message))

        member _.DeleteFile(remotePath) =
            let target = if isLocal computer then remotePath else adminSharePath computer remotePath
            try
                if File.Exists target then File.Delete target
            with ex ->
                raiseMru "MRU407" (sprintf "%s: %s" target (firstLine ex.Message))

        member _.Dispose() =
            for share in smbConnections do
                Mpr.WNetCancelConnection2(share, 0, true) |> ignore
            regProv.Dispose()
            processClass.Dispose()

let connect (settings: ConnectionSettings) (computer: string) : IRemoteSession =
    new WmiSession(computer, settings) :> IRemoteSession
