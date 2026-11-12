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

let private uninstallScript =
    """
param([string] $ProductCode, [string] $LogFolder, [string] $LogFile)
$ErrorActionPreference = 'Stop'
try {
    if (-not (Test-Path -LiteralPath $LogFolder)) { New-Item -ItemType Directory -Path $LogFolder -Force | Out-Null }
    $msiArgs = @('/x', $ProductCode, '/qn', '/norestart', '/l*v', "`"$LogFile`"")
    $p = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $msiArgs -Wait -PassThru -WindowStyle Hidden
}
catch {
    throw "MRU402: $($_.Exception.Message)"
}
$p.ExitCode
"""

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

        member _.Uninstall(productCode, logFolder, logFile) =
            let result =
                invoke
                    "MRU403"
                    settings.Timeout
                    uninstallScript
                    [ "ProductCode", box productCode; "LogFolder", box logFolder; "LogFile", box logFile ]
            match result |> List.tryLast with
            | Some o when not (isNull o) ->
                match Int32.TryParse(string o) with
                | true, code -> code
                | _ -> raiseMru "MRU404" (sprintf "Unexpected result from remote msiexec: '%O'" o)
            | _ -> raiseMru "MRU404" "The remote command returned no exit code."

        member _.Dispose() =
            (try runspace.Close() with _ -> ())
            runspace.Dispose()

let connect (settings: ConnectionSettings) (computer: string) : IRemoteSession =
    new PsRemotingSession(computer, settings) :> IRemoteSession
