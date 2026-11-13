/// Transport 1: PowerShell Remoting over WinRM / WS-Management (HTTP or HTTPS),
/// hosted in-process via the PowerShell SDK. Same mechanism as Invoke-Command.
module MsiRemoteUninstall.PsRemotingTransport

open System
open System.Management.Automation
open System.Management.Automation.Runspaces
open MsiRemoteUninstall.Domain
open MsiRemoteUninstall.Cli
open MsiRemoteUninstall.Transport
open MsiRemoteUninstall.Errors

let private inventoryScript =
    """
$ErrorActionPreference = 'Stop'
$guid = '^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$'
$keys = @(
    @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*';             Arch = '64-bit' },
    @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'; Arch = '32-bit' }
)
foreach ($k in $keys) {
    Get-ItemProperty -Path $k.Path -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match $guid -and $_.PSObject.Properties['DisplayName'] -and $_.DisplayName } |
        ForEach-Object {
            $o = $_
            $get = { param($n) $p = $o.PSObject.Properties[$n]; if ($p -and $null -ne $p.Value) { [string]$p.Value } else { '' } }
            [pscustomobject]@{
                ProductCode  = $o.PSChildName
                DisplayName  = [string]$o.DisplayName
                Version      = & $get 'DisplayVersion'
                Publisher    = & $get 'Publisher'
                InstallDate  = & $get 'InstallDate'
                Architecture = $k.Arch
            }
        }
}
"""

let private msiexecScript =
    """
param([string] $Arguments, [string] $LogFolder)
$ErrorActionPreference = 'Stop'
try {
    if (-not (Test-Path -LiteralPath $LogFolder)) { New-Item -ItemType Directory -Path $LogFolder -Force | Out-Null }
    $p = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
}
catch {
    throw "MRU402: $($_.Exception.Message)"
}
$p.ExitCode
"""

/// Writes one base64 chunk; the first chunk creates / truncates the file.
let private uploadChunkScript =
    """
param([string] $Path, [string] $Data, [bool] $First)
$ErrorActionPreference = 'Stop'
try {
    $dir = Split-Path -Parent $Path
    if ($First -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $bytes = [Convert]::FromBase64String($Data)
    $mode = if ($First) { [IO.FileMode]::Create } else { [IO.FileMode]::Append }
    $fs = New-Object IO.FileStream($Path, $mode, [IO.FileAccess]::Write)
    try { $fs.Write($bytes, 0, $bytes.Length) } finally { $fs.Dispose() }
    (Get-Item -LiteralPath $Path).Length
}
catch {
    throw "MRU406: $($_.Exception.Message)"
}
"""

let private deleteScript =
    """
param([string] $Path)
if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force -ErrorAction Stop }
"""

/// Size of one upload chunk (raw bytes; base64 adds a third).
let private chunkSize = 512 * 1024

let private prop (o: PSObject) (name: string) =
    match o.Properties.[name] with
    | null -> ""
    | p ->
        match p.Value with
        | null -> ""
        | v -> string v

let private toMechanism auth =
    match auth with
    | AuthMode.Default -> AuthenticationMechanism.Default
    | AuthMode.Negotiate -> AuthenticationMechanism.Negotiate
    | AuthMode.Kerberos -> AuthenticationMechanism.Kerberos
    | AuthMode.CredSsp -> AuthenticationMechanism.Credssp
    | AuthMode.Basic -> AuthenticationMechanism.Basic

type PsRemotingSession(computer: string, settings: ConnectionSettings) =
    let connectionInfo =
        let credential =
            match settings.Credential with
            | Some c -> PSCredential(c.UserName, c.Password)
            | None -> null // current Windows logon
        let port = defaultArg settings.Port (if settings.UseSsl then 5986 else 5985)
        let ci =
            WSManConnectionInfo(
                settings.UseSsl,
                computer,
                port,
                "/wsman",
                "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
                credential
            )
        ci.AuthenticationMechanism <- toMechanism settings.Auth
        ci.OpenTimeout <- int (TimeSpan.FromMinutes 1.0).TotalMilliseconds
        if settings.SkipCertCheck then
            ci.SkipCACheck <- true
            ci.SkipCNCheck <- true
            ci.SkipRevocationCheck <- true
        ci

    let runspace = RunspaceFactory.CreateRunspace(connectionInfo)
    do runspace.Open()

    /// Runs a script remotely; raises `timeoutCode` if it does not finish in time.
    let invoke (timeoutCode: string) (timeout: TimeSpan) (script: string) (parameters: (string * obj) list) : PSObject list =
        use ps = PowerShell.Create()
        ps.Runspace <- runspace
        ps.AddScript(script) |> ignore
        for (n, v) in parameters do
            ps.AddParameter(n, v) |> ignore
        let ar = ps.BeginInvoke()
        if not (ar.AsyncWaitHandle.WaitOne timeout) then
            (try ps.Stop() with _ -> ())
            raiseMru timeoutCode (sprintf "Remote command did not finish within %.0f minute(s)." timeout.TotalMinutes)
        ps.EndInvoke(ar) |> List.ofSeq

    let remoteName =
        try
            match invoke "MRU310" (TimeSpan.FromMinutes 2.0) "$env:COMPUTERNAME" [] with
            | o :: _ when not (isNull o) && string o <> "" -> string o
            | _ -> computer
        with _ -> computer

    interface IRemoteSession with
        member _.ComputerName = remoteName

        member _.GetInstalledProducts() =
            invoke "MRU401" (TimeSpan.FromMinutes 5.0) inventoryScript []
            |> Seq.filter (fun o -> not (isNull o))
            |> Seq.map (fun o ->
                { ProductCode = (prop o "ProductCode").ToUpperInvariant()
                  DisplayName = prop o "DisplayName"
                  Version = prop o "Version"
                  Publisher = prop o "Publisher"
                  InstallDate = prop o "InstallDate"
                  Architecture = prop o "Architecture" })
            |> distinctProducts

        member _.RunMsiexec(arguments, logFolder) =
            let result =
                invoke "MRU403" settings.Timeout msiexecScript [ "Arguments", box arguments; "LogFolder", box logFolder ]
            match result |> List.tryLast with
            | Some o when not (isNull o) ->
                match Int32.TryParse(string o) with
                | true, code -> code
                | _ -> raiseMru "MRU404" (sprintf "Unexpected result from remote msiexec: '%O'" o)
            | _ -> raiseMru "MRU404" "The remote command returned no exit code."

        member _.UploadFile(localPath, remotePath) =
            use input = IO.File.OpenRead localPath
            let buffer = Array.zeroCreate<byte> chunkSize
            let mutable first = true
            let mutable remoteLength = 0L
            let mutable read = input.Read(buffer, 0, buffer.Length)
            // an empty file still needs one (empty) chunk to be created
            while read > 0 || first do
                let data = Convert.ToBase64String(buffer, 0, read)
                let result =
                    invoke "MRU406" (TimeSpan.FromMinutes 5.0) uploadChunkScript
                        [ "Path", box remotePath; "Data", box data; "First", box first ]
                remoteLength <-
                    match result |> List.tryLast with
                    | Some o when not (isNull o) -> Convert.ToInt64(o.BaseObject)
                    | _ -> -1L
                first <- false
                read <- input.Read(buffer, 0, buffer.Length)
            if remoteLength <> input.Length then
                raiseMru "MRU406" (sprintf "Size mismatch after upload: %d bytes local, %d bytes on the server." input.Length remoteLength)

        member _.DeleteFile(remotePath) =
            invoke "MRU407" (TimeSpan.FromMinutes 2.0) deleteScript [ "Path", box remotePath ] |> ignore

        member _.Dispose() =
            (try runspace.Close() with _ -> ())
            runspace.Dispose()

let connect (settings: ConnectionSettings) (computer: string) : IRemoteSession =
    new PsRemotingSession(computer, settings) :> IRemoteSession
