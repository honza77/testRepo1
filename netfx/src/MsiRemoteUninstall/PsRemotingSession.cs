using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace MsiRemoteUninstall
{
    /// <summary>
    /// Transport 1: PowerShell Remoting over WinRM (HTTP 5985 / HTTPS 5986), hosted in-process
    /// through the Windows PowerShell 5.1 engine - the same mechanism as Invoke-Command.
    /// </summary>
    public sealed class PsRemotingSession : IRemoteSession
    {
        private const string InventoryScript = @"
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
";

        private const string MsiexecScript = @"
param([string] $Arguments, [string] $LogFolder)
$ErrorActionPreference = 'Stop'
try {
    if (-not (Test-Path -LiteralPath $LogFolder)) { New-Item -ItemType Directory -Path $LogFolder -Force | Out-Null }
    $p = Start-Process -FilePath ""$env:SystemRoot\System32\msiexec.exe"" -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
}
catch {
    throw ""MRU402: $($_.Exception.Message)""
}
$p.ExitCode
";

        private const string UploadChunkScript = @"
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
    throw ""MRU406: $($_.Exception.Message)""
}
";

        private const string DeleteScript = @"
param([string] $Path)
if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force -ErrorAction Stop }
";

        private const int ChunkSize = 512 * 1024;

        private readonly ConnectionSettings _settings;
        private readonly Runspace _runspace;

        public PsRemotingSession(string computer, ConnectionSettings settings)
        {
            _settings = settings;
            var credential = settings.Credential == null ? null : new PSCredential(settings.Credential.UserName, settings.Credential.Password);
            var port = settings.Port ?? (settings.UseSsl ? 5986 : 5985);
            var ci = new WSManConnectionInfo(settings.UseSsl, computer, port, "/wsman",
                                             "http://schemas.microsoft.com/powershell/Microsoft.PowerShell", credential)
            {
                AuthenticationMechanism = ToMechanism(settings.Auth),
                OpenTimeout = (int)TimeSpan.FromMinutes(1).TotalMilliseconds
            };
            if (settings.SkipCertCheck)
            {
                ci.SkipCACheck = true;
                ci.SkipCNCheck = true;
                ci.SkipRevocationCheck = true;
            }

            _runspace = RunspaceFactory.CreateRunspace(ci);
            try
            {
                _runspace.Open();
            }
            catch
            {
                _runspace.Dispose();
                throw;
            }

            try
            {
                var name = Invoke("MRU310", TimeSpan.FromMinutes(2), "$env:COMPUTERNAME").FirstOrDefault()?.ToString();
                ComputerName = string.IsNullOrEmpty(name) ? computer : name;
            }
            catch
            {
                ComputerName = computer;
            }
        }

        public string ComputerName { get; }

        private static AuthenticationMechanism ToMechanism(AuthMode auth)
        {
            switch (auth)
            {
                case AuthMode.Negotiate: return AuthenticationMechanism.Negotiate;
                case AuthMode.Kerberos: return AuthenticationMechanism.Kerberos;
                case AuthMode.CredSsp: return AuthenticationMechanism.Credssp;
                case AuthMode.Basic: return AuthenticationMechanism.Basic;
                default: return AuthenticationMechanism.Default;
            }
        }

        /// <summary>Runs a script remotely; raises <paramref name="timeoutCode"/> if it does not finish in time.</summary>
        private List<PSObject> Invoke(string timeoutCode, TimeSpan timeout, string script, params (string Name, object Value)[] parameters)
        {
            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;
                ps.AddScript(script);
                foreach (var (name, value) in parameters) ps.AddParameter(name, value);
                var ar = ps.BeginInvoke();
                if (!ar.AsyncWaitHandle.WaitOne(timeout))
                {
                    try { ps.Stop(); } catch { /* best effort */ }
                    throw new MruException(timeoutCode, $"Remote command did not finish within {timeout.TotalMinutes:0} minute(s).");
                }
                return ps.EndInvoke(ar).ToList();
            }
        }

        private static string Prop(PSObject o, string name)
        {
            var p = o.Properties[name];
            return p?.Value == null ? "" : p.Value.ToString();
        }

        public List<InstalledProduct> GetInstalledProducts() =>
            Invoke("MRU401", TimeSpan.FromMinutes(5), InventoryScript)
                .Where(o => o != null)
                .Select(o => new InstalledProduct
                {
                    ProductCode = Prop(o, "ProductCode").ToUpperInvariant(),
                    DisplayName = Prop(o, "DisplayName"),
                    Version = Prop(o, "Version"),
                    Publisher = Prop(o, "Publisher"),
                    InstallDate = Prop(o, "InstallDate"),
                    Architecture = Prop(o, "Architecture")
                })
                .GroupBy(p => p.ProductCode).Select(g => g.First())
                .ToList();

        public int RunMsiexec(string arguments, string logFolder)
        {
            var result = Invoke("MRU403", _settings.Timeout, MsiexecScript, ("Arguments", arguments), ("LogFolder", logFolder));
            var last = result.LastOrDefault();
            if (last == null) throw new MruException("MRU404", "The remote command returned no exit code.");
            if (int.TryParse(last.ToString(), out var code)) return code;
            throw new MruException("MRU404", $"Unexpected result from remote msiexec: '{last}'");
        }

        public void UploadFile(string localPath, string remotePath)
        {
            using (var input = File.OpenRead(localPath))
            {
                var buffer = new byte[ChunkSize];
                var first = true;
                long remoteLength = -1;
                var read = input.Read(buffer, 0, buffer.Length);
                // an empty file still needs one (empty) chunk to be created
                while (read > 0 || first)
                {
                    var data = Convert.ToBase64String(buffer, 0, read);
                    var result = Invoke("MRU406", TimeSpan.FromMinutes(5), UploadChunkScript, ("Path", remotePath), ("Data", data), ("First", first));
                    var last = result.LastOrDefault();
                    remoteLength = last == null ? -1 : Convert.ToInt64(last.BaseObject);
                    first = false;
                    read = input.Read(buffer, 0, buffer.Length);
                }
                if (remoteLength != input.Length)
                    throw new MruException("MRU406", $"Size mismatch after upload: {input.Length} bytes local, {remoteLength} bytes on the server.");
            }
        }

        public void DeleteFile(string remotePath) =>
            Invoke("MRU407", TimeSpan.FromMinutes(2), DeleteScript, ("Path", remotePath));

        public void Dispose()
        {
            try { _runspace.Close(); } catch { /* ignore */ }
            _runspace.Dispose();
        }
    }
}
