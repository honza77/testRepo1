<#
.SYNOPSIS
    Uninstalls (or lists) MSI products on remote Windows servers - version 2
    with coded error handling, explanations and remediation hints.

.DESCRIPTION
    Same features as Uninstall-RemoteMsi.ps1:
      * servers and MSIs from the command line and/or list files
      * MSIs by display name (wildcards) or product code GUID
      * list / search mode (-ListInstalled, -Filter, -FilterMode Substring|Wildcard|Regex)
      * credentials from a pop-up (default) or the console (-ConsoleCredential)
      * -WhatIf / -Confirm, parallel execution, CSV report

    New in version 2:
      * Every problem gets an error code (MRU### or MSI####) with a plain-language
        explanation and remediation steps. The codes are identical to the .NET tool
        (dotnet/MsiRemoteUninstall) and documented in ERROR-CODES.md.
      * -ListErrorCodes and -ExplainError <code> show the catalog.
      * A DNS + TCP port check runs before connecting (-SkipPreflight to disable),
        so "name not found", "port closed" and "access denied" are told apart.
      * msiexec timeout (-TimeoutMinutes), WinRM -Port, -Authentication and
        -SkipCertificateCheck options.
      * A summary of all codes encountered, with remediation, at the end of the run.
      * Process exit codes:
            0  success                      4  aborted at the confirmation question
            1  at least one failure         5  no server could be reached
            2  invalid input                6  unexpected internal error
            3  credential problem           10 success, but a reboot is required

.PARAMETER ComputerName
    One or more server host names.

.PARAMETER ComputerListPath
    Text file with one server host name per line.

.PARAMETER MsiName
    One or more MSI display names (wildcards allowed) or product code GUIDs.

.PARAMETER MsiListPath
    Text file with one MSI display name or product code per line.

.PARAMETER ListInstalled
    List the MSI products installed on the servers instead of uninstalling.

.PARAMETER Filter
    With -ListInstalled: one or more search patterns (case-insensitive). A product is
    listed if its DisplayName, Publisher or ProductCode matches ANY pattern.

.PARAMETER FilterMode
    Substring (default): field must contain the text.
    Wildcard: PowerShell -like pattern, must match the WHOLE field (use *text*).
    Regex: .NET regular expression, matches anywhere unless anchored.

.PARAMETER Credential
    Credentials for the servers. If omitted you are prompted.

.PARAMETER UserName
    User name to pre-fill when prompting.

.PARAMETER ConsoleCredential
    Prompt for user name / password in the console instead of a pop-up window.

.PARAMETER UseSSL
    Connect to WinRM over HTTPS (default port 5986).

.PARAMETER SkipCertificateCheck
    With -UseSSL: accept untrusted / mismatched certificates (testing only).

.PARAMETER Port
    WinRM port (default 5985, or 5986 with -UseSSL).

.PARAMETER Authentication
    WinRM authentication: Default, Negotiate, Kerberos, CredSSP, Basic.

.PARAMETER ThrottleLimit
    Maximum number of servers processed in parallel. Default 16.

.PARAMETER TimeoutMinutes
    Maximum time for one msiexec run before it is reported as MRU403. Default 60.

.PARAMETER SkipPreflight
    Skip the DNS + TCP port check before connecting.

.PARAMETER ReportPath
    CSV report path. Default .\MsiUninstallReport_<timestamp>.csv (or MsiInventory_... in list mode).

.PARAMETER RemoteLogFolder
    msiexec log folder on the servers. Default C:\Windows\Temp\MsiUninstallLogs.

.PARAMETER ListErrorCodes
    Show all error codes and process exit codes, then exit.

.PARAMETER ExplainError
    Show the explanation and remediation for one error code (e.g. MRU303, MSI1603 or 1603), then exit.

.EXAMPLE
    .\Uninstall-RemoteMsi.v2.ps1 -ComputerListPath .\servers.txt -MsiListPath .\msis.txt

.EXAMPLE
    .\Uninstall-RemoteMsi.v2.ps1 -ComputerName SRV01 -ListInstalled -FilterMode Regex -Filter '^Contoso' -ConsoleCredential

.EXAMPLE
    .\Uninstall-RemoteMsi.v2.ps1 -ExplainError MRU307

.EXAMPLE
    .\Uninstall-RemoteMsi.v2.ps1 -ListErrorCodes

.NOTES
    Requirements: PowerShell Remoting (WinRM) enabled on the servers; the account must be a
    local administrator there. Input files: blank lines and lines starting with '#' are ignored.
    The error catalog below is generated from dotnet/MsiRemoteUninstall/Errors.fs
    (MsiRemoteUninstall --errors json) - edit it there, not here.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string[]] $ComputerName,
    [string] $ComputerListPath,
    [string[]] $MsiName,
    [string] $MsiListPath,

    [switch] $ListInstalled,
    [string[]] $Filter,
    [ValidateSet('Substring', 'Wildcard', 'Regex')]
    [string] $FilterMode = 'Substring',

    [System.Management.Automation.PSCredential]
    [System.Management.Automation.Credential()]
    $Credential = [System.Management.Automation.PSCredential]::Empty,
    [string] $UserName,
    [switch] $ConsoleCredential,

    [switch] $UseSSL,
    [switch] $SkipCertificateCheck,
    [ValidateRange(1, 65535)]
    [int] $Port,
    [ValidateSet('Default', 'Negotiate', 'Kerberos', 'CredSSP', 'Basic')]
    [string] $Authentication = 'Default',
    [ValidateRange(1, 256)]
    [int] $ThrottleLimit = 16,
    [ValidateRange(1, 1440)]
    [int] $TimeoutMinutes = 60,
    [switch] $SkipPreflight,

    [string] $ReportPath,
    [string] $RemoteLogFolder = 'C:\Windows\Temp\MsiUninstallLogs',

    [switch] $ListErrorCodes,
    [string] $ExplainError
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#region Error catalog (generated - do not edit by hand)
# <error-catalog>
$script:ErrorCatalogJson = @'
{
  "ErrorCodes": [
    {
      "AppliesTo": ".NET app",
      "Category": "Input",
      "Code": "MRU101",
      "Explanation": "An argument was not recognised, is missing its value, or its value is outside the allowed range. (In the PowerShell script, PowerShell itself validates parameters before the script starts.)",
      "Remediation": [
        "Compare the spelling with the help: MsiRemoteUninstall --help.",
        "Put values that contain spaces or commas in quotes, e.g. -m \"Contoso Agent*\".",
        "Numeric ranges: --port 1-65535, --throttle 1-256, --timeout 1-1440 (minutes)."
      ],
      "Severity": "Error",
      "Title": "Invalid command-line argument"
    },
    {
      "AppliesTo": "All",
      "Category": "Input",
      "Code": "MRU102",
      "Explanation": "The list of target servers is empty, so there is nothing to do.",
      "Remediation": [
        "Pass servers with --computer / -ComputerName, or a file with --computer-list / -ComputerListPath.",
        "If you used a list file, make sure it is not empty and not completely commented out (lines starting with # are ignored)."
      ],
      "Severity": "Error",
      "Title": "No servers specified"
    },
    {
      "AppliesTo": "All",
      "Category": "Input",
      "Code": "MRU103",
      "Explanation": "Uninstall mode needs at least one MSI display name or product code.",
      "Remediation": [
        "Pass MSIs with --msi / -MsiName, or a file with --msi-list / -MsiListPath.",
        "If you only want to see what is installed, use list mode: --list / -ListInstalled."
      ],
      "Severity": "Error",
      "Title": "No MSIs specified"
    },
    {
      "AppliesTo": "All",
      "Category": "Input",
      "Code": "MRU104",
      "Explanation": "A server or MSI list file does not exist at the given path.",
      "Remediation": [
        "Check the path and file name for typos.",
        "Relative paths are resolved against the current directory - use a full path if unsure.",
        "Quote paths that contain spaces."
      ],
      "Severity": "Error",
      "Title": "List file not found"
    },
    {
      "AppliesTo": "All",
      "Category": "Input",
      "Code": "MRU105",
      "Explanation": "The list file exists but could not be opened or read.",
      "Remediation": [
        "Check that your account has read permission on the file.",
        "Close the file if another program (e.g. Excel) holds it locked.",
        "Save the file as plain text (UTF-8 or ANSI), one entry per line."
      ],
      "Severity": "Error",
      "Title": "List file cannot be read"
    },
    {
      "AppliesTo": "All",
      "Category": "Input",
      "Code": "MRU106",
      "Explanation": "A --filter / -Filter pattern is not valid for the chosen filter mode (regex or wildcard). Nothing was contacted.",
      "Remediation": [
        "Regex mode: escape special characters with a backslash, e.g. C\\+\\+ or \\(x64\\), and balance all brackets and parentheses.",
        "Wildcard mode: close every '[' with ']'; to match a literal '[' or '*' escape it with a backtick, e.g. `[.",
        "If you just want 'contains this text', use the default substring mode (no special characters at all)."
      ],
      "Severity": "Error",
      "Title": "Invalid search filter pattern"
    },
    {
      "AppliesTo": "All",
      "Category": "Input",
      "Code": "MRU107",
      "Explanation": "An MSI entry that is not a product code is treated as a wildcard pattern, and this one is not valid (usually an unclosed '[').",
      "Remediation": [
        "Close every '[' with ']' or escape it with a backtick: `[.",
        "Alternatively use the product code GUID, which is matched exactly. List mode (--list / -ListInstalled) shows the product codes."
      ],
      "Severity": "Error",
      "Title": "Invalid MSI name pattern"
    },
    {
      "AppliesTo": "All",
      "Category": "Input",
      "Code": "MRU108",
      "Explanation": "An option was given that has no effect in this combination (for example --filter without --list, or --ssl with the WMI transport). The run continues without it.",
      "Remediation": [
        "Remove the option, or add the option it depends on (see the warning text)."
      ],
      "Severity": "Warning",
      "Title": "Option ignored"
    },
    {
      "AppliesTo": "All",
      "Category": "Credentials",
      "Code": "MRU201",
      "Explanation": "The credential prompt was cancelled or the user name was left empty.",
      "Remediation": [
        "Run again and enter the user name as DOMAIN\\user, user@domain.fqdn, or SERVER\\user for a local account.",
        "Pre-fill the user name with --user / -UserName.",
        "To use your current Windows logon instead, use --current-user (.NET) or run the script from a session already logged on as the right account and pass -Credential (Get-Credential)."
      ],
      "Severity": "Error",
      "Title": "No user name entered / credential prompt cancelled"
    },
    {
      "AppliesTo": "All",
      "Category": "Credentials",
      "Code": "MRU202",
      "Explanation": "The password was empty. Empty passwords cannot be used for remote management.",
      "Remediation": [
        "Run again and type the password (it is not shown while typing).",
        "For unattended runs use --password-env VAR (.NET) or pass a prepared -Credential object (PowerShell)."
      ],
      "Severity": "Error",
      "Title": "No password entered"
    },
    {
      "AppliesTo": ".NET app",
      "Category": "Credentials",
      "Code": "MRU203",
      "Explanation": "--password-env was given, but that environment variable does not exist or is empty in this process.",
      "Remediation": [
        "Set the variable in the same shell before starting the tool, e.g. PowerShell: $env:MRU_PW = '...'; cmd: set MRU_PW=...",
        "Check the variable name passed to --password-env (no $ or % around it)."
      ],
      "Severity": "Error",
      "Title": "Password environment variable missing"
    },
    {
      "AppliesTo": "All",
      "Category": "Credentials",
      "Code": "MRU204",
      "Explanation": "There is no interactive console to ask for the password (for example when started from a scheduled task, a service or a non-interactive remote shell).",
      "Remediation": [
        "Run the tool from an interactive console.",
        ".NET: pass --user together with --password-env, or use --current-user.",
        "PowerShell: pass -Credential with a PSCredential object, or use -ConsoleCredential in a console host."
      ],
      "Severity": "Error",
      "Title": "Cannot prompt for credentials"
    },
    {
      "AppliesTo": "All",
      "Category": "Connection",
      "Code": "MRU301",
      "Explanation": "DNS (or NetBIOS) could not translate the server name into an IP address, so no connection was attempted.",
      "Remediation": [
        "Check the server name for typos.",
        "Try the fully qualified name, e.g. srv01.contoso.local.",
        "Test name resolution: Resolve-DnsName srv01 (PowerShell) or nslookup srv01.",
        "Check the client's DNS servers / VPN connection, or that the server still exists."
      ],
      "Severity": "Error",
      "Title": "Server name cannot be resolved"
    },
    {
      "AppliesTo": "All",
      "Category": "Connection",
      "Code": "MRU302",
      "Explanation": "The name resolved, but a TCP connection to the remote management port was refused or timed out (WinRM: 5985 HTTP / 5986 HTTPS, WMI/DCOM: 135). The server is offline, a firewall blocks the port, or nothing is listening on it.",
      "Remediation": [
        "Check that the server is running: Test-Connection srv01.",
        "Test the port: Test-NetConnection srv01 -Port 5985 (or 5986 with SSL, 135 for WMI).",
        "WinRM: on the server run Enable-PSRemoting -Force, which also opens the 'Windows Remote Management (HTTP-In)' firewall rule. For HTTPS an HTTPS listener and a 5986 firewall rule are needed.",
        "WMI: enable the firewall rule group 'Windows Management Instrumentation (WMI)' on the server.",
        "Check network firewalls / security groups between client and server.",
        "If the port check itself is the problem (e.g. through a proxy), skip it with --skip-preflight / -SkipPreflight."
      ],
      "Severity": "Error",
      "Title": "Server unreachable or management port closed"
    },
    {
      "AppliesTo": "All",
      "Category": "Connection",
      "Code": "MRU303",
      "Explanation": "The server was reached and the credentials were checked, but the account is not allowed to do this. Uninstalling software requires local administrator rights on the server.",
      "Remediation": [
        "Make sure the account is a member of the local Administrators group on the server (directly or via a domain group).",
        "Local (non-domain) administrator accounts are filtered by UAC remote restrictions: use a domain account, or set HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\\LocalAccountTokenFilterPolicy = 1 (DWORD) on the server.",
        "WinRM: check the endpoint permissions on the server: Get-PSSessionConfiguration Microsoft.PowerShell | Select Permission.",
        "WMI: check namespace security (wmimgmt.msc -> WMI Control -> Security, root\\cimv2 and root\\default need 'Remote Enable') and DCOM remote launch/activation permissions (dcomcnfg).",
        "Verify the credentials are the ones you intended (--user / -UserName)."
      ],
      "Severity": "Error",
      "Title": "Access denied"
    },
    {
      "AppliesTo": "All",
      "Category": "Connection",
      "Code": "MRU304",
      "Explanation": "The server rejected the user name or password: wrong password, unknown user, locked or disabled account, expired password, or the domain controller could not be contacted.",
      "Remediation": [
        "Re-enter the password carefully (Caps Lock, keyboard layout).",
        "Use the form DOMAIN\\user or user@domain.fqdn; for a local account on the server use SERVER\\user.",
        "Check whether the account is locked out, disabled or its password expired (Get-ADUser user -Properties LockedOut,Enabled,PasswordExpired).",
        "Kerberos requires the clocks of client, server and domain controller to be within 5 minutes - check time synchronisation (w32tm /query /status)."
      ],
      "Severity": "Error",
      "Title": "Logon failure"
    },
    {
      "AppliesTo": "PowerShell Remoting",
      "Category": "Connection",
      "Code": "MRU305",
      "Explanation": "The WinRM port answers, but WinRM did not accept the request: the service is stopped, there is no listener for this protocol (HTTP vs HTTPS) or port, the listener is bound to another address, or PowerShell remoting endpoints are disabled.",
      "Remediation": [
        "On the server: Enable-PSRemoting -Force (or winrm quickconfig).",
        "Check the service: Get-Service WinRM (should be Running, startup Automatic).",
        "List listeners: winrm enumerate winrm/config/listener - for --ssl / -UseSSL an HTTPS listener is required.",
        "Check the port option (--port / -Port) matches the listener.",
        "From the client: Test-WSMan srv01 (add -UseSSL for HTTPS).",
        "Alternatively use the WMI transport (.NET: -t wmi)."
      ],
      "Severity": "Error",
      "Title": "WinRM service not available"
    },
    {
      "AppliesTo": "PowerShell Remoting with SSL",
      "Category": "Connection",
      "Code": "MRU306",
      "Explanation": "The HTTPS connection to WinRM failed because the server certificate is not trusted, has expired, or its name does not match the server name you used.",
      "Remediation": [
        "Connect with the exact name in the certificate's Subject / SAN - usually the FQDN (srv01.contoso.local).",
        "Make sure the issuing CA is trusted on the client (Local Computer\\Trusted Root Certification Authorities).",
        "Renew the certificate if it has expired and update the HTTPS listener's thumbprint.",
        "For testing only, skip the checks with --skip-cert-check / -SkipCertificateCheck (traffic is still encrypted, but the server is not verified)."
      ],
      "Severity": "Error",
      "Title": "TLS / certificate problem"
    },
    {
      "AppliesTo": "All",
      "Category": "Connection",
      "Code": "MRU307",
      "Explanation": "The client and server could not agree on an authentication method. Typical causes: connecting by IP address or alias (Kerberos needs the real host name), the client or server is not in the domain and the server is not in TrustedHosts, CredSSP is not enabled, or Basic authentication is used over plain HTTP.",
      "Remediation": [
        "Connect using the server's real host name or FQDN, not an IP address or DNS alias.",
        "For workgroup servers or IP addresses add the server to TrustedHosts on the client (as admin): Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value 'srv01' -Concatenate - or use HTTPS (--ssl / -UseSSL), which does not need TrustedHosts.",
        "CredSSP: enable it on both sides (Enable-WSManCredSSP -Role Client / -Role Server).",
        "Basic: only works with local accounts and requires HTTPS or AllowUnencrypted on the server - prefer the default (Negotiate).",
        "Check the server's SPNs if Kerberos fails for a correct name: setspn -L srv01."
      ],
      "Severity": "Error",
      "Title": "Authentication method could not be used"
    },
    {
      "AppliesTo": "WMI transport",
      "Category": "Connection",
      "Code": "MRU308",
      "Explanation": "DCOM could not reach the WMI service. Port 135 may be open, but DCOM also needs the dynamic RPC port range (49152-65535 by default), or the WMI service is not running.",
      "Remediation": [
        "On the server enable the firewall rule group 'Windows Management Instrumentation (WMI)' (it covers the dynamic ports for WMI).",
        "Allow the dynamic RPC ports on network firewalls between client and server.",
        "Check the WMI service on the server: Get-Service Winmgmt.",
        "Or use the PowerShell Remoting transport (one fixed port, 5985/5986)."
      ],
      "Severity": "Error",
      "Title": "RPC server unavailable (WMI / DCOM)"
    },
    {
      "AppliesTo": "WMI transport",
      "Category": "Connection",
      "Code": "MRU309",
      "Explanation": "WMI on the server reported a problem with a namespace, class or provider (root\\cimv2 or root\\default / StdRegProv). The WMI repository may be damaged.",
      "Remediation": [
        "On the server check the repository: winmgmt /verifyrepository (and winmgmt /salvagerepository if inconsistent).",
        "Restart the WMI service: Restart-Service Winmgmt -Force.",
        "Test locally on the server: Get-CimInstance Win32_OperatingSystem.",
        "Use the PowerShell Remoting transport as a workaround."
      ],
      "Severity": "Error",
      "Title": "WMI error on the server"
    },
    {
      "AppliesTo": "All",
      "Category": "Connection",
      "Code": "MRU310",
      "Explanation": "The server accepted the network connection but did not complete the session setup in time. The server may be overloaded, hanging, or the network is very slow.",
      "Remediation": [
        "Retry later - check the server's CPU / memory load.",
        "Test the connection directly: Test-WSMan srv01 or Get-CimInstance -ComputerName srv01 Win32_OperatingSystem.",
        "Reduce the parallelism with --throttle / -ThrottleLimit if many servers share a slow link."
      ],
      "Severity": "Error",
      "Title": "Connection timed out"
    },
    {
      "AppliesTo": "All",
      "Category": "Connection",
      "Code": "MRU399",
      "Explanation": "Connecting failed for a reason that does not fit the other categories. The detail message contains the original error from Windows.",
      "Remediation": [
        "Read the detail message in the report - it usually names the cause.",
        "Test the connection manually: Test-WSMan srv01, Enter-PSSession srv01, or Get-CimInstance -ComputerName srv01 Win32_OperatingSystem.",
        "On the server check the event logs Microsoft-Windows-WinRM/Operational and System."
      ],
      "Severity": "Error",
      "Title": "Other connection error"
    },
    {
      "AppliesTo": "All",
      "Category": "Remote operation",
      "Code": "MRU401",
      "Explanation": "The connection worked, but reading the list of installed MSI products from the server's registry (Uninstall keys) failed.",
      "Remediation": [
        "Make sure the account is a local administrator on the server.",
        "Retry - a transient WMI / WinRM problem is possible.",
        "Test on the server: Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'.",
        "WMI transport: see MRU309 for WMI repository problems."
      ],
      "Severity": "Error",
      "Title": "Could not read installed products"
    },
    {
      "AppliesTo": "All",
      "Category": "Remote operation",
      "Code": "MRU402",
      "Explanation": "The uninstall process could not be started. WMI Win32_Process.Create codes: 2 = access denied, 3 = insufficient privilege, 8 = unknown failure, 9 = path not found, 21 = invalid parameter. It can also mean the remote log folder could not be created.",
      "Remediation": [
        "Make sure the account is a local administrator on the server.",
        "Check that the remote log folder (--remote-log-folder / -RemoteLogFolder) is a valid local path on the server and its drive exists.",
        "Check whether AppLocker / WDAC / antivirus blocks msiexec.exe, cmd.exe or reg.exe for remote processes.",
        "Verify %SystemRoot%\\System32\\msiexec.exe exists on the server."
      ],
      "Severity": "Error",
      "Title": "Could not start msiexec on the server"
    },
    {
      "AppliesTo": "All",
      "Category": "Remote operation",
      "Code": "MRU403",
      "Explanation": "msiexec was still running when the timeout expired. It may be waiting for a dialog that cannot be shown (a custom action ignoring /qn), blocked by a locked file or service, or the product is simply very large. The process is left running on the server.",
      "Remediation": [
        "Look at the msiexec log on the server (path in the report) - the last lines show what it is doing.",
        "Check for running installer processes on the server: Get-Process msiexec.",
        "Stop the hung msiexec process if needed, or reboot the server to clear pending operations.",
        "Increase the timeout: --timeout / -TimeoutMinutes (minutes)."
      ],
      "Severity": "Error",
      "Title": "msiexec did not finish in time"
    },
    {
      "AppliesTo": "All",
      "Category": "Remote operation",
      "Code": "MRU404",
      "Explanation": "msiexec ran but its exit code could not be read back. WMI transport: the temporary value under HKLM\\SOFTWARE\\MsiRemoteUninstall\\ExitCodes was not written (cmd.exe or reg.exe blocked, or the process was killed). PowerShell Remoting: the remote command returned no exit code.",
      "Remediation": [
        "Check whether the product is still installed: run in list mode (--list / -ListInstalled).",
        "Read the msiexec log on the server (path in the report).",
        "Check AppLocker / WDAC / antivirus rules for cmd.exe and reg.exe."
      ],
      "Severity": "Error",
      "Title": "msiexec result could not be determined"
    },
    {
      "AppliesTo": "All",
      "Category": "Remote operation",
      "Code": "MRU405",
      "Explanation": "An unexpected error occurred while running the command on the server, or the session broke (server rebooted, network dropped, WinRM memory or idle limits reached).",
      "Remediation": [
        "Read the detail message in the report.",
        "Run again - products that were already removed are reported as NotFound, so re-running is safe.",
        "Check the server's event logs (Application, System, Microsoft-Windows-WinRM/Operational).",
        "If the session broke, check whether the server rebooted (some uninstalls force a reboot)."
      ],
      "Severity": "Error",
      "Title": "Remote command failed"
    },
    {
      "AppliesTo": "All",
      "Category": "Local",
      "Code": "MRU501",
      "Explanation": "The CSV report could not be saved. The results were still shown on screen and (PowerShell) returned as objects.",
      "Remediation": [
        "Check that the target folder exists and you can write to it.",
        "Close the file if it is open in Excel.",
        "Choose another path with --report / -ReportPath."
      ],
      "Severity": "Warning",
      "Title": "Report could not be written"
    },
    {
      "AppliesTo": "All",
      "Category": "Local",
      "Code": "MRU502",
      "Explanation": "The confirmation question was answered with No, so nothing was uninstalled.",
      "Remediation": [
        "Run again and confirm with 'y', or skip the question with -y / --yes (.NET) or -Confirm:$false (PowerShell).",
        "Use --what-if / -WhatIf first to see what would be removed."
      ],
      "Severity": "Info",
      "Title": "Aborted by user"
    },
    {
      "AppliesTo": ".NET app",
      "Category": "Local",
      "Code": "MRU503",
      "Explanation": "The WinRM client and WMI/DCOM APIs used by the tool exist only on Windows. This happens when the .NET tool is started on Linux or macOS.",
      "Remediation": [
        "Run the tool on Windows 10/11 or Windows Server 2016 or newer.",
        "Or use the PowerShell script from a Windows machine."
      ],
      "Severity": "Error",
      "Title": "Remote management not available on this OS"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1601",
      "Explanation": "msiexec could not contact the Windows Installer service on the server. The service may be disabled or its registration damaged, or Windows is in Safe Mode.",
      "Remediation": [
        "On the server: Get-Service msiserver - the startup type must not be Disabled.",
        "Re-register Windows Installer (as admin): msiexec /unregister then msiexec /regserver.",
        "Reboot the server and retry."
      ],
      "Severity": "Error",
      "Title": "Windows Installer service not accessible"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1602",
      "Explanation": "The uninstall was cancelled. With a silent uninstall (/qn) this usually means a custom action of the product cancelled it.",
      "Remediation": [
        "Read the msiexec log on the server (path in the report) for the reason.",
        "Check the vendor's documentation for prerequisites for uninstalling (e.g. a removal password or a service that must be stopped)."
      ],
      "Severity": "Error",
      "Title": "Uninstall cancelled"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1603",
      "Explanation": "A generic failure inside the uninstall. The most common causes: a custom action of the product failed, files or services are in use, a pending reboot, or missing permissions inside the product's own uninstall logic.",
      "Remediation": [
        "Open the verbose msiexec log (path in the report) and search for 'Return value 3' - the lines just above it name the failing action.",
        "Stop the product's processes and services, then retry.",
        "Reboot the server (clears pending file operations) and retry.",
        "Use the vendor's removal / cleanup tool if the product's uninstaller is broken."
      ],
      "Severity": "Error",
      "Title": "Fatal error during uninstall"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1605",
      "Explanation": "msiexec reports the product is not installed. It was removed in the meantime (for example by an earlier entry in the same run), or it was installed per-user under another account.",
      "Remediation": [
        "Usually nothing to do. Check with list mode (--list / -ListInstalled) if unsure."
      ],
      "Severity": "Info",
      "Title": "Product is not installed"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1612",
      "Explanation": "Windows Installer needs the original MSI package to uninstall, but its cached copy (C:\\Windows\\Installer) and the original source location are missing.",
      "Remediation": [
        "Copy the exact same version of the original .msi back to its original source path, or run msiexec /x <path-to-product.msi> on the server.",
        "Use the vendor's cleanup tool, or Microsoft's 'Program Install and Uninstall troubleshooter'.",
        "Avoid cleaning C:\\Windows\\Installer manually in future."
      ],
      "Severity": "Error",
      "Title": "Installation source unavailable"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1618",
      "Explanation": "Only one Windows Installer operation can run at a time. Another installation (Windows Update, software deployment, a hung msiexec) was still running after all retries.",
      "Remediation": [
        "Wait until the other installation finishes and run again.",
        "Check on the server: Get-Process msiexec, and Windows Update / deployment agent activity.",
        "If an msiexec process is hung, stop it or reboot the server."
      ],
      "Severity": "Error",
      "Title": "Another installation is in progress"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1619",
      "Explanation": "The cached MSI package of the product could not be opened - it is missing, inaccessible or corrupt.",
      "Remediation": [
        "Same as MSI1612: restore the original .msi of the same version, or uninstall using the package path.",
        "Check antivirus quarantine for C:\\Windows\\Installer\\*.msi files."
      ],
      "Severity": "Error",
      "Title": "Installation package could not be opened"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1620",
      "Explanation": "The cached MSI package exists but is not a valid Windows Installer package (corrupt).",
      "Remediation": [
        "Restore the original .msi of the same version and uninstall using its path, or use the vendor's cleanup tool."
      ],
      "Severity": "Error",
      "Title": "Installation package is invalid"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1622",
      "Explanation": "msiexec could not create the verbose log file in the remote log folder.",
      "Remediation": [
        "Check that the remote log folder (--remote-log-folder / -RemoteLogFolder) is a valid, writable local path on the server.",
        "Check free disk space on the server.",
        "Avoid very long paths or special characters in the log folder path."
      ],
      "Severity": "Error",
      "Title": "Log file could not be opened"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1624",
      "Explanation": "The product was installed with a transform (.mst) whose cached copy is missing or invalid, so Windows Installer cannot open the product configuration.",
      "Remediation": [
        "Restore the transform file to the path used at install time.",
        "Use the vendor's cleanup tool."
      ],
      "Severity": "Error",
      "Title": "Error applying transforms"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1625",
      "Explanation": "A Group Policy or local policy prevents this uninstall (for example 'Turn off Windows Installer' or 'Prohibit removal of updates').",
      "Remediation": [
        "Check the applied policies on the server: gpresult /h report.html.",
        "Ask the policy owner for an exception, or run the uninstall with an account the policy allows."
      ],
      "Severity": "Error",
      "Title": "Prohibited by system policy"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1633",
      "Explanation": "The package is not supported on this processor architecture / platform.",
      "Remediation": [
        "Check the msiexec log; this is unusual for an uninstall and may indicate a corrupt product registration.",
        "Use the vendor's cleanup tool."
      ],
      "Severity": "Error",
      "Title": "Platform not supported"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1639",
      "Explanation": "msiexec rejected its command line.",
      "Remediation": [
        "Check the remote log folder path (--remote-log-folder / -RemoteLogFolder) for unusual characters such as quotes.",
        "If the problem persists, report it as a bug including the product code."
      ],
      "Severity": "Error",
      "Title": "Invalid command line argument"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI1641",
      "Explanation": "The uninstall succeeded and the installer started a reboot of the server, despite /norestart (some products force it).",
      "Remediation": [
        "Nothing to do - the server is restarting. Check it comes back online."
      ],
      "Severity": "Warning",
      "Title": "Uninstalled - reboot initiated"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI3010",
      "Explanation": "The uninstall succeeded, but some files or settings are only removed after the server restarts.",
      "Remediation": [
        "Schedule a reboot of the server, e.g. Restart-Computer -ComputerName srv01 at a convenient time."
      ],
      "Severity": "Warning",
      "Title": "Uninstalled - reboot required"
    },
    {
      "AppliesTo": "All",
      "Category": "msiexec exit code",
      "Code": "MSI-OTHER",
      "Explanation": "msiexec returned an exit code that is not described here.",
      "Remediation": [
        "Look up the code: net helpmsg <code>, or search Microsoft's 'MsiExec.exe and InstMsi.exe error messages' page.",
        "Read the verbose msiexec log on the server (path in the report)."
      ],
      "Severity": "Error",
      "Title": "Other msiexec error"
    },
    {
      "AppliesTo": "All",
      "Category": "Internal",
      "Code": "MRU999",
      "Explanation": "An error occurred that the tool does not handle specifically - most likely a bug.",
      "Remediation": [
        "Read the detail message, it contains the original error.",
        "Run again; if it happens again, report it together with the detail message and the command line (without passwords)."
      ],
      "Severity": "Error",
      "Title": "Unexpected internal error"
    }
  ],
  "ExitCodes": [
    {
      "Description": "Everything completed successfully.",
      "ExitCode": 0,
      "Title": "Success"
    },
    {
      "Description": "At least one server or MSI failed - see the report and the listed error codes.",
      "ExitCode": 1,
      "Title": "Partial failure"
    },
    {
      "Description": "Invalid arguments or input files (MRU1xx). Nothing was contacted.",
      "ExitCode": 2,
      "Title": "Invalid input"
    },
    {
      "Description": "No usable credentials (MRU2xx). Nothing was contacted.",
      "ExitCode": 3,
      "Title": "Credential problem"
    },
    {
      "Description": "The confirmation question was answered with No (MRU502).",
      "ExitCode": 4,
      "Title": "Aborted"
    },
    {
      "Description": "Every server failed to connect (MRU3xx).",
      "ExitCode": 5,
      "Title": "No server reachable"
    },
    {
      "Description": "Unexpected error (MRU999).",
      "ExitCode": 6,
      "Title": "Internal error"
    },
    {
      "Description": "Everything succeeded, but at least one server needs a reboot (MSI3010 / MSI1641).",
      "ExitCode": 10,
      "Title": "Success, reboot required"
    }
  ]
}
'@
# </error-catalog>
#endregion

#region Error helpers
$script:Catalog = $script:ErrorCatalogJson | ConvertFrom-Json
$script:ErrorIndex = @{}
foreach ($entry in $script:Catalog.ErrorCodes) { $script:ErrorIndex[$entry.Code] = $entry }

# Process exit codes
$ExitOk = 0; $ExitPartialFailure = 1; $ExitInvalidInput = 2; $ExitCredentials = 3
$ExitAborted = 4; $ExitNoServerReachable = 5; $ExitInternal = 6; $ExitOkRebootRequired = 10

function Get-ErrorInfo {
    param([string] $Code)
    if (-not $Code) { return $null }
    $c = $Code.Trim().ToUpperInvariant()
    if ($c -match '^\d+$') { $c = "MSI$c" }
    if ($script:ErrorIndex.ContainsKey($c)) { return $script:ErrorIndex[$c] }
    return $null
}

function Get-MsiErrorCode {
    param([int] $ExitCode)
    $c = "MSI$ExitCode"
    if ($script:ErrorIndex.ContainsKey($c)) { $c } else { 'MSI-OTHER' }
}

function Format-Wrapped {
    param([string] $Text, [string] $Indent = '  ', [int] $Width = 100)
    $lines = New-Object System.Collections.Generic.List[string]
    $line = $Indent
    $cont = ' ' * $Indent.Length
    foreach ($word in ($Text -split '\s+' | Where-Object { $_ })) {
        if (($line.Length + $word.Length + 1) -gt $Width -and $line.Trim()) {
            $lines.Add($line.TrimEnd()); $line = $cont
        }
        if ($line.EndsWith(' ')) { $line += $word } else { $line += " $word" }
    }
    $lines.Add($line.TrimEnd())
    $lines -join [Environment]::NewLine
}

function Show-ErrorExplanation {
    param([Parameter(Mandatory)] $Info)
    Write-Host ("{0}  {1}" -f $Info.Code, $Info.Title) -ForegroundColor Cyan
    Write-Host ("Category: {0}   Severity: {1}   Applies to: {2}" -f $Info.Category, $Info.Severity, $Info.AppliesTo)
    Write-Host ''
    Write-Host 'What it means:'
    Write-Host (Format-Wrapped -Text $Info.Explanation)
    Write-Host ''
    Write-Host 'How to fix it:'
    $i = 0
    foreach ($r in $Info.Remediation) { $i++; Write-Host (Format-Wrapped -Text $r -Indent ("  $i. ")) }
    Write-Host ''
}

function Show-ErrorList {
    Write-Host 'Error codes (use -ExplainError <code> for details and remediation):' -ForegroundColor Cyan
    foreach ($group in ($script:Catalog.ErrorCodes | Group-Object Category)) {
        Write-Host ''
        Write-Host $group.Name
        foreach ($e in $group.Group) { Write-Host ('  {0,-10} {1,-8} {2}' -f $e.Code, $e.Severity, $e.Title) }
    }
    Write-Host ''
    Write-Host 'Process exit codes:' -ForegroundColor Cyan
    foreach ($x in $script:Catalog.ExitCodes) { Write-Host ('  {0,-3} {1,-25} {2}' -f $x.ExitCode, $x.Title, $x.Description) }
}

# Terminating error with catalog code: carries code + detail to the top-level handler.
class MruError : System.Exception {
    [string] $Code
    [string] $Detail
    MruError([string] $code, [string] $detail) : base("[$code] $detail") { $this.Code = $code; $this.Detail = $detail }
}

function Stop-Mru {
    param([string] $Code, [string] $Detail)
    throw [MruError]::new($Code, $Detail)
}

function Write-CodedMessage {
    param([string] $Computer, [string] $Code, [string] $Detail)
    $info = Get-ErrorInfo $Code
    if (-not $info) { $info = Get-ErrorInfo 'MRU999' }
    $prefix = if ($Computer) { "[$Computer] " } else { '' }
    $text = "{0}[{1}] {2}: {3}" -f $prefix, $Code, $info.Title, $Detail
    switch ($info.Severity) {
        'Error'   { Write-Host "ERROR: $text" -ForegroundColor Red }
        'Warning' { Write-Host "WARNING: $text" -ForegroundColor Yellow }
        default   { Write-Host $text }
    }
}

function Write-CodeSummary {
    param([string[]] $Codes)
    $distinct = @($Codes | Where-Object { $_ } | Group-Object | Sort-Object Name)
    if ($distinct.Count -eq 0) { return }
    Write-Host ''
    Write-Host 'Problems / notices found in this run:' -ForegroundColor Yellow
    foreach ($g in $distinct) {
        $info = Get-ErrorInfo $g.Name
        if (-not $info) { $info = Get-ErrorInfo 'MRU999' }
        $color = switch ($info.Severity) { 'Error' { 'Red' } 'Warning' { 'Yellow' } default { 'Gray' } }
        Write-Host ''
        Write-Host ("[{0}] {1}  ({2} occurrence(s))" -f $g.Name, $info.Title, $g.Count) -ForegroundColor $color
        $i = 0
        foreach ($r in $info.Remediation) { $i++; Write-Host ("   {0}. {1}" -f $i, $r) }
    }
    Write-Host ''
    Write-Host 'Full explanations: -ExplainError <code>  (or see ERROR-CODES.md)'
}

function Get-FirstLine {
    param([string] $Text)
    if (-not $Text) { return '' }
    (($Text -split "[`r`n]+") | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ' '
}

# Maps WinRM / PowerShell Remoting errors to catalog codes (same rules as the .NET tool).
function Get-WinRmErrorCode {
    param([int] $Code, [string] $Message)
    $m = "$Message"
    if ($Code -eq 5 -or $m -match 'Access is denied') { return 'MRU303' }
    if ($Code -in 1326, 1327, 1330, 1331, 1909 -or $m -match 'user name or password is incorrect|logon failure|account is locked|account is disabled|password has expired') { return 'MRU304' }
    if ($Code -eq 12175 -or $m -match 'certificate') { return 'MRU306' }
    if ($Code -eq -2144108316 -or $m -match 'TrustedHosts|Kerberos|CredSSP|authentication') { return 'MRU307' }
    if ($Code -eq -2144108250 -or $m -match 'timed out|timeout') { return 'MRU310' }
    if ($Code -eq -2144108103 -or $m -match 'cannot be resolved|could not be resolved') { return 'MRU301' }
    if ($Code -eq -2144108526 -or $m -match 'WinRM|WS-Management') { return 'MRU305' }
    return 'MRU399'
}

function Get-ErrorRecordCode {
    param([System.Management.Automation.ErrorRecord] $Record, [string] $Fallback)
    $ex = $Record.Exception
    while ($ex -is [System.AggregateException] -and $ex.InnerException) { $ex = $ex.InnerException }
    $detail = Get-FirstLine $ex.Message
    if ($detail -match '^(MRU\d{3}):\s*(.*)$') { return @($Matches[1], $Matches[2]) }
    if ($ex -is [System.Management.Automation.Remoting.PSRemotingTransportException]) {
        return @((Get-WinRmErrorCode -Code $ex.ErrorCode -Message $ex.Message), $detail)
    }
    if ($ex -is [System.UnauthorizedAccessException]) { return @('MRU303', $detail) }
    if ($ex -is [System.TimeoutException]) { return @('MRU310', $detail) }
    return @($Fallback, $detail)
}
#endregion

#region Help switches
if ($ListErrorCodes) { Show-ErrorList; exit $ExitOk }
if ($ExplainError) {
    $info = Get-ErrorInfo $ExplainError
    if ($info) { Show-ErrorExplanation -Info $info; exit $ExitOk }
    Write-Host "ERROR: Unknown error code '$ExplainError'. Use -ListErrorCodes to list all codes." -ForegroundColor Red
    exit $ExitInvalidInput
}
#endregion

#region Input helpers
function Read-ListFile {
    param([Parameter(Mandatory)][string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Stop-Mru 'MRU104' ("{0} (current directory: {1})" -f $Path, (Get-Location))
    }
    try { $lines = Get-Content -LiteralPath $Path -ErrorAction Stop }
    catch { Stop-Mru 'MRU105' ("{0}: {1}" -f $Path, (Get-FirstLine $_.Exception.Message)) }
    $lines | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }
}

function Merge-List {
    param([string[]] $Inline, [string] $FilePath)
    $items = @()
    if ($Inline)   { $items += $Inline | ForEach-Object { $_.Trim() } | Where-Object { $_ } }
    if ($FilePath) { $items += Read-ListFile -Path $FilePath }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($i in $items) { if ($seen.Add($i)) { $i } }
}

function Read-ConsoleCredential {
    param([string] $DefaultUserName, [string] $Message)
    Write-Host $Message -ForegroundColor Cyan
    $user = $DefaultUserName
    if ($user) { Write-Host "User name: $user" }
    else { $user = "$(Read-Host -Prompt 'User name (e.g. DOMAIN\user)')".Trim() }
    if (-not $user) { Stop-Mru 'MRU201' 'The user name was empty.' }
    $password = Read-Host -Prompt "Password for $user" -AsSecureString
    if (-not $password -or $password.Length -eq 0) { Stop-Mru 'MRU202' 'The password was empty.' }
    New-Object System.Management.Automation.PSCredential ($user, $password)
}

function Test-ProductCode { param([string] $Text) $Text -match '^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$' }

# DNS + TCP check of all servers in parallel. Returns hashtable server -> $null (ok) or @(code, detail).
function Test-Preflight {
    param([string[]] $Servers, [int] $TcpPort, [int] $TimeoutSeconds = 5)
    $probes = foreach ($s in $Servers) {
        $client = New-Object System.Net.Sockets.TcpClient
        $task = $null; $startError = $null
        try { $task = $client.ConnectAsync($s, $TcpPort) } catch { $startError = $_.Exception }
        [pscustomobject]@{ Server = $s; Client = $client; Task = $task; StartError = $startError }
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    foreach ($p in $probes) {
        if ($p.Task) {
            $remaining = [int][Math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds)
            try { [void]$p.Task.Wait($remaining) } catch { }
        }
    }
    $result = @{}
    foreach ($p in $probes) {
        $outcome = $null
        $ex = $p.StartError
        if (-not $ex -and $p.Task -and $p.Task.IsFaulted) { $ex = $p.Task.Exception }
        while ($ex -and ($ex -is [System.AggregateException] -or $ex -is [System.Management.Automation.MethodInvocationException]) -and $ex.InnerException) { $ex = $ex.InnerException }
        if ($ex) {
            $sock = $ex -as [System.Net.Sockets.SocketException]
            if ($sock -and $sock.SocketErrorCode -in 'HostNotFound', 'TryAgain', 'NoData') {
                $outcome = @('MRU301', ("'{0}': {1}" -f $p.Server, (Get-FirstLine $ex.Message)))
            }
            elseif ($sock -and $sock.SocketErrorCode -eq 'ConnectionRefused') {
                $outcome = @('MRU302', ("TCP port {0} on {1}: connection refused (nothing listening, or blocked by a firewall)." -f $TcpPort, $p.Server))
            }
            else {
                $outcome = @('MRU302', ("TCP port {0} on {1}: {2}" -f $TcpPort, $p.Server, (Get-FirstLine $ex.Message)))
            }
        }
        elseif (-not $p.Task -or -not $p.Task.IsCompleted) {
            $outcome = @('MRU302', ("TCP port {0} on {1} did not answer within {2} s (server offline or port filtered by a firewall)." -f $TcpPort, $p.Server, $TimeoutSeconds))
        }
        $p.Client.Dispose()
        $result[$p.Server] = $outcome
    }
    $result
}
#endregion

#region Remote script blocks
# Inventory: returns all MSI products (filtering happens locally), or one error object.
$inventoryScript = {
    $ErrorActionPreference = 'Stop'
    try {
        $guid = '^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$'
        $keys = @(
            @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*';             Arch = '64-bit' },
            @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'; Arch = '32-bit' }
        )
        $all = foreach ($k in $keys) {
            Get-ItemProperty -Path $k.Path -ErrorAction SilentlyContinue |
                Where-Object { $_.PSChildName -match $guid -and $_.PSObject.Properties['DisplayName'] -and $_.DisplayName } |
                ForEach-Object {
                    $o = $_
                    $get = { param($n) $p = $o.PSObject.Properties[$n]; if ($p -and $null -ne $p.Value) { [string]$p.Value } else { '' } }
                    [pscustomobject]@{
                        Kind         = 'Product'
                        ComputerName = $env:COMPUTERNAME
                        DisplayName  = [string]$o.DisplayName
                        Version      = & $get 'DisplayVersion'
                        Publisher    = & $get 'Publisher'
                        InstallDate  = & $get 'InstallDate'
                        Architecture = $k.Arch
                        ProductCode  = $o.PSChildName.ToUpperInvariant()
                        ErrorCode    = ''
                        Message      = ''
                    }
                }
        }
        $all | Sort-Object ProductCode -Unique
    }
    catch {
        [pscustomobject]@{ Kind = 'Error'; ComputerName = $env:COMPUTERNAME; ErrorCode = 'MRU401'; Message = $_.Exception.Message }
    }
}

# Uninstall: returns one row per MSI entry / matched product. Rows with Status 'Completed'
# carry the raw msiexec exit code; it is interpreted locally with the error catalog.
$uninstallScript = {
    param([string[]] $MsiList, [bool] $DryRun, [string] $LogFolder, [int] $TimeoutMinutes)
    $ErrorActionPreference = 'Stop'
    $guidPattern = '^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$'

    function New-Result {
        param($Msi, $Product, $Status, $ExitCode, $ErrorCode, $Message, $LogFile)
        [pscustomobject]@{
            ComputerName = $env:COMPUTERNAME
            Msi          = $Msi
            DisplayName  = if ($Product) { $Product.DisplayName } else { '' }
            Version      = if ($Product) { $Product.DisplayVersion } else { '' }
            ProductCode  = if ($Product) { $Product.ProductCode } else { '' }
            Status       = $Status
            ExitCode     = $ExitCode
            ErrorCode    = $ErrorCode
            Message      = $Message
            LogFile      = $LogFile
        }
    }

    try {
        $installed = @(
            Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                                   'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
                Where-Object { $_.PSChildName -match $guidPattern -and $_.PSObject.Properties['DisplayName'] -and $_.DisplayName } |
                ForEach-Object {
                    [pscustomobject]@{
                        ProductCode    = $_.PSChildName.ToUpperInvariant()
                        DisplayName    = [string]$_.DisplayName
                        DisplayVersion = if ($_.PSObject.Properties['DisplayVersion']) { [string]$_.DisplayVersion } else { '' }
                    }
                } | Sort-Object ProductCode -Unique
        )
    }
    catch {
        New-Result -Msi ($MsiList -join '; ') -Status 'ExecutionError' -ErrorCode 'MRU401' -Message $_.Exception.Message
        return
    }

    $handled = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

    foreach ($msi in $MsiList) {
        if ($msi -match $guidPattern) {
            $code = '{' + $msi.Trim('{', '}').ToUpperInvariant() + '}'
            $found = @($installed | Where-Object { $_.ProductCode -eq $code })
        }
        else {
            $found = @($installed | Where-Object { $_.DisplayName -like $msi })
        }

        if ($found.Count -eq 0) {
            New-Result -Msi $msi -Status 'NotFound' -ErrorCode '' -Message 'No matching MSI product installed.'
            continue
        }

        foreach ($p in $found) {
            if (-not $handled.Add($p.ProductCode)) {
                New-Result -Msi $msi -Product $p -Status 'NotInstalled' -ErrorCode '' -Message 'Already handled by an earlier MSI entry.'
                continue
            }
            if ($DryRun) {
                New-Result -Msi $msi -Product $p -Status 'WouldUninstall' -ErrorCode '' -Message 'WhatIf: not uninstalled.'
                continue
            }

            $logFile = Join-Path $LogFolder ("{0}_{1:yyyyMMdd_HHmmss}.log" -f $p.ProductCode.Trim('{', '}'), (Get-Date))
            try {
                if (-not (Test-Path -LiteralPath $LogFolder)) { New-Item -ItemType Directory -Path $LogFolder -Force | Out-Null }
            }
            catch {
                New-Result -Msi $msi -Product $p -Status 'ExecutionError' -ErrorCode 'MRU402' -Message ("Cannot create log folder '{0}': {1}" -f $LogFolder, $_.Exception.Message) -LogFile $logFile
                continue
            }

            $msiArgs = @('/x', $p.ProductCode, '/qn', '/norestart', '/l*v', "`"$logFile`"")
            $attempt = 0
            $exit = $null
            $failure = $null
            do {
                $attempt++
                try {
                    $proc = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $msiArgs -PassThru -WindowStyle Hidden
                    $null = $proc.Handle   # keep the handle so ExitCode is available later
                }
                catch {
                    $failure = @('MRU402', $_.Exception.Message); break
                }
                if (-not $proc.WaitForExit($TimeoutMinutes * 60000)) {
                    $failure = @('MRU403', ("msiexec (PID {0}) still running after {1} minute(s); it was left running." -f $proc.Id, $TimeoutMinutes)); break
                }
                $exit = $proc.ExitCode
                if ($null -eq $exit) { $failure = @('MRU404', 'msiexec exit code could not be read.'); break }
                if ($exit -eq 1618 -and $attempt -lt 5) { Start-Sleep -Seconds 30 }
            } while ($exit -eq 1618 -and $attempt -lt 5)

            if ($failure) {
                New-Result -Msi $msi -Product $p -Status 'ExecutionError' -ErrorCode $failure[0] -Message $failure[1] -LogFile $logFile
            }
            else {
                New-Result -Msi $msi -Product $p -Status 'Completed' -ExitCode $exit -ErrorCode '' -Message '' -LogFile $logFile
            }
        }
    }
}
#endregion

#region Main
$columns = 'ComputerName', 'Msi', 'DisplayName', 'Version', 'ProductCode', 'Status', 'ExitCode', 'ErrorCode', 'Message', 'LogFile'

function New-LocalRow {
    param($Computer, $Msi, $Status, $ErrorCode, $Message)
    [pscustomobject]@{
        ComputerName = $Computer; Msi = $Msi; DisplayName = ''; Version = ''; ProductCode = ''
        Status = $Status; ExitCode = $null; ErrorCode = $ErrorCode; Message = $Message; LogFile = ''
    }
}

function Save-Report {
    param($Rows, [string] $Path)
    try {
        $Rows | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8 -WhatIf:$false
        Write-Host "Report saved to: $Path" -ForegroundColor Green
        $true
    }
    catch {
        Write-CodedMessage -Code 'MRU501' -Detail ("{0}: {1}" -f $Path, (Get-FirstLine $_.Exception.Message))
        $false
    }
}

function Invoke-Main {
    # ---- input
    $servers = @(Merge-List -Inline $ComputerName -FilePath $ComputerListPath)
    $msis    = @(Merge-List -Inline $MsiName      -FilePath $MsiListPath)
    if ($servers.Count -eq 0) { Stop-Mru 'MRU102' 'Use -ComputerName and/or -ComputerListPath.' }
    if ($msis.Count -eq 0 -and -not $ListInstalled) { Stop-Mru 'MRU103' 'Use -MsiName and/or -MsiListPath, or -ListInstalled to list installed MSIs.' }

    if ($Filter -and -not $ListInstalled) { Write-CodedMessage -Code 'MRU108' -Detail '-Filter is only used together with -ListInstalled.' }
    if ($SkipCertificateCheck -and -not $UseSSL) { Write-CodedMessage -Code 'MRU108' -Detail '-SkipCertificateCheck has no effect without -UseSSL.' }
    if ($Credential -ne [System.Management.Automation.PSCredential]::Empty -and ($UserName -or $ConsoleCredential)) {
        Write-CodedMessage -Code 'MRU108' -Detail '-Credential was supplied; -UserName / -ConsoleCredential are ignored.'
    }

    # ---- validate patterns before contacting anything
    $filters = @($Filter | Where-Object { $_ -and $_.Trim() } | ForEach-Object { $_.Trim() })
    if ($ListInstalled) {
        foreach ($f in $filters) {
            try {
                switch ($FilterMode) {
                    'Regex'    { [void][regex]::new($f, 'IgnoreCase') }
                    'Wildcard' { [void][System.Management.Automation.WildcardPattern]::new($f, 'IgnoreCase').IsMatch('') }
                }
            }
            catch {
                $ex = $_.Exception; if ($ex.InnerException) { $ex = $ex.InnerException }
                Stop-Mru 'MRU106' ("{0} filter '{1}': {2}" -f $FilterMode, $f, $ex.Message)
            }
        }
    }
    else {
        foreach ($m in $msis) {
            if (Test-ProductCode $m) { continue }
            try { [void][System.Management.Automation.WildcardPattern]::new($m, 'IgnoreCase').IsMatch('') }
            catch {
                $ex = $_.Exception; if ($ex.InnerException) { $ex = $ex.InnerException }
                Stop-Mru 'MRU107' ("'{0}': {1}" -f $m, $ex.Message)
            }
        }
    }

    $reportFile = $ReportPath
    if (-not $reportFile) {
        $prefix = if ($ListInstalled) { 'MsiInventory' } else { 'MsiUninstallReport' }
        $reportFile = Join-Path -Path (Get-Location) -ChildPath ("{0}_{1:yyyyMMdd_HHmmss}.csv" -f $prefix, (Get-Date))
    }

    Write-Host ''
    Write-Host ("Servers ({0}): {1}" -f $servers.Count, ($servers -join ', ')) -ForegroundColor Cyan
    if ($ListInstalled) {
        if ($filters.Count) { Write-Host ("Filter ({0}, any of): {1}" -f $FilterMode.ToLower(), ($filters -join ', ')) -ForegroundColor Cyan }
        else { Write-Host 'Filter: none (listing all MSI products)' -ForegroundColor Cyan }
    }
    else { Write-Host ("MSIs    ({0}): {1}" -f $msis.Count, ($msis -join ', ')) -ForegroundColor Cyan }
    Write-Host ''

    # ---- credentials
    $cred = $Credential
    if ($cred -eq [System.Management.Automation.PSCredential]::Empty) {
        $message = 'Enter credentials with local admin rights on the target servers'
        try {
            if ($ConsoleCredential) {
                $cred = Read-ConsoleCredential -DefaultUserName $UserName -Message $message
            }
            else {
                $p = @{ Message = $message }
                if ($UserName) { $p['UserName'] = $UserName }
                $cred = Get-Credential @p
            }
        }
        catch [MruError] { throw }
        catch {
            # no interactive host (scheduled task, -NonInteractive, service, ...)
            Stop-Mru 'MRU204' (Get-FirstLine $_.Exception.Message)
        }
        if (-not $cred) { Stop-Mru 'MRU201' 'The credential prompt was cancelled.' }
        if (-not $cred.UserName) { Stop-Mru 'MRU201' 'The user name was empty.' }
        if ($cred.Password.Length -eq 0) { Stop-Mru 'MRU202' 'The password was empty.' }
    }

    # ---- confirmation
    $dryRun = [bool]$WhatIfPreference
    if (-not $ListInstalled) {
        if ($dryRun) { Write-Host 'WhatIf mode: products will be discovered but NOT uninstalled.' -ForegroundColor Yellow }
        elseif (-not $PSCmdlet.ShouldProcess(("{0} server(s)" -f $servers.Count), ("Uninstall MSI(s): {0}" -f ($msis -join ', ')))) {
            Stop-Mru 'MRU502' 'Nothing was uninstalled.'
        }
    }

    # ---- preflight
    $failedRows = New-Object System.Collections.Generic.List[object]
    $targets = $servers
    if (-not $SkipPreflight) {
        $tcpPort = if ($Port) { $Port } elseif ($UseSSL) { 5986 } else { 5985 }
        Write-Host ("Checking name resolution and TCP port {0}..." -f $tcpPort) -ForegroundColor Cyan
        $pre = Test-Preflight -Servers $servers -TcpPort $tcpPort
        $targets = @($servers | Where-Object { -not $pre[$_] })
        foreach ($s in $servers) {
            if ($pre[$s]) {
                Write-CodedMessage -Computer $s -Code $pre[$s][0] -Detail $pre[$s][1]
                $failedRows.Add((New-LocalRow -Computer $s -Msi ($msis -join '; ') -Status 'ConnectionFailed' -ErrorCode $pre[$s][0] -Message $pre[$s][1]))
            }
        }
    }

    # ---- remote execution
    $invokeParams = @{
        Credential     = $cred
        ThrottleLimit  = $ThrottleLimit
        Authentication = $Authentication
        ErrorAction    = 'SilentlyContinue'
        ErrorVariable  = 'remoteErrors'
    }
    if ($UseSSL) { $invokeParams['UseSSL'] = $true }
    if ($Port)   { $invokeParams['Port'] = $Port }
    if ($UseSSL -and $SkipCertificateCheck) {
        $invokeParams['SessionOption'] = New-PSSessionOption -SkipCACheck -SkipCNCheck -SkipRevocationCheck
    }

    $remoteErrors = @()
    $output = @()
    if ($targets.Count -gt 0) {
        Write-Host ("Connecting to {0} server(s)..." -f $targets.Count) -ForegroundColor Cyan
        try {
            if ($ListInstalled) {
                $output = @(Invoke-Command @invokeParams -ComputerName $targets -ScriptBlock $inventoryScript)
            }
            else {
                $output = @(Invoke-Command @invokeParams -ComputerName $targets -ScriptBlock $uninstallScript `
                            -ArgumentList (@(, $msis) + $dryRun + $RemoteLogFolder + $TimeoutMinutes))
            }
        }
        catch {
            # parameter-level failures of Invoke-Command (e.g. invalid authentication combination)
            $code, $detail = Get-ErrorRecordCode -Record $_ -Fallback 'MRU399'
            foreach ($s in $targets) {
                Write-CodedMessage -Computer $s -Code $code -Detail $detail
                $failedRows.Add((New-LocalRow -Computer $s -Msi ($msis -join '; ') -Status 'ConnectionFailed' -ErrorCode $code -Message $detail))
            }
        }
    }

    foreach ($err in $remoteErrors) {
        $target = $null
        if ($err.PSObject.Properties['TargetObject'] -and $err.TargetObject) { $target = [string]$err.TargetObject }
        if (-not $target -and $err.PSObject.Properties['OriginInfo'] -and $err.OriginInfo) { $target = $err.OriginInfo.PSComputerName }
        $isTransport = $err.Exception -is [System.Management.Automation.Remoting.PSRemotingTransportException]
        $code, $detail = Get-ErrorRecordCode -Record $err -Fallback $(if ($isTransport) { 'MRU399' } else { 'MRU405' })
        Write-CodedMessage -Computer $target -Code $code -Detail $detail
        $status = if ($isTransport) { 'ConnectionFailed' } else { 'ExecutionError' }
        $failedRows.Add((New-LocalRow -Computer $target -Msi ($msis -join '; ') -Status $status -ErrorCode $code -Message $detail))
    }

    $connectionFailedServers = @($failedRows | Where-Object Status -eq 'ConnectionFailed' | Select-Object -ExpandProperty ComputerName -Unique)

    # ---- list mode
    if ($ListInstalled) {
        $matchers = foreach ($f in $filters) {
            switch ($FilterMode) {
                'Regex'    { $r = [regex]::new($f, 'IgnoreCase'); { param($s) $r.IsMatch($s) }.GetNewClosure() }
                'Wildcard' { $w = [System.Management.Automation.WildcardPattern]::new($f, 'IgnoreCase'); { param($s) $w.IsMatch($s) }.GetNewClosure() }
                default    { $t = $f; { param($s) $s.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0 }.GetNewClosure() }
            }
        }
        $matchers = @($matchers)
        $products = @($output | Where-Object { $_.Kind -eq 'Product' } | Where-Object {
            if ($matchers.Count -eq 0) { return $true }
            $p = $_
            foreach ($m in $matchers) {
                foreach ($field in @($p.DisplayName, $p.Publisher, $p.ProductCode)) {
                    if ($field -and (& $m ([string]$field))) { return $true }
                }
            }
            $false
        } | Select-Object ComputerName, DisplayName, Version, Publisher, InstallDate, Architecture, ProductCode, ErrorCode, Message)

        $inventoryErrors = @($output | Where-Object { $_.Kind -eq 'Error' } | ForEach-Object {
            Write-CodedMessage -Computer $_.ComputerName -Code $_.ErrorCode -Detail (Get-FirstLine $_.Message)
            [pscustomobject]@{ ComputerName = $_.ComputerName; DisplayName = ''; Version = ''; Publisher = ''; InstallDate = ''
                               Architecture = ''; ProductCode = ''; ErrorCode = $_.ErrorCode; Message = (Get-FirstLine $_.Message) }
        })
        $listFailures = @($failedRows | ForEach-Object {
            [pscustomobject]@{ ComputerName = $_.ComputerName; DisplayName = ''; Version = ''; Publisher = ''; InstallDate = ''
                               Architecture = ''; ProductCode = ''; ErrorCode = $_.ErrorCode; Message = $_.Message }
        }) + $inventoryErrors

        if ($products.Count) {
            $products | Sort-Object ComputerName, DisplayName |
                Format-Table ComputerName, DisplayName, Version, Publisher, ProductCode -AutoSize | Out-Host
            $products | Group-Object ComputerName |
                Select-Object @{n = 'ComputerName'; e = { $_.Name } }, @{n = 'MatchingMsis'; e = { $_.Count } } |
                Format-Table -AutoSize | Out-Host
        }
        else { Write-Host 'No matching MSI products found.' -ForegroundColor Yellow }
        if ($listFailures.Count) {
            Write-Host ("{0} server(s) could not be queried:" -f $listFailures.Count) -ForegroundColor Yellow
            $listFailures | Format-Table ComputerName, ErrorCode, Message -AutoSize -Wrap | Out-Host
        }

        $codes = @($listFailures | ForEach-Object { $_.ErrorCode })
        $allRows = @($products) + @($listFailures)
        if ($allRows.Count -and -not (Save-Report -Rows $allRows -Path $reportFile)) { $codes += 'MRU501' }
        Write-CodeSummary -Codes $codes

        $failedCount = @($listFailures | Select-Object -ExpandProperty ComputerName -Unique).Count
        $script:ExitCode = if ($failedCount -eq 0) { $ExitOk } elseif ($connectionFailedServers.Count -eq $servers.Count) { $ExitNoServerReachable } else { $ExitPartialFailure }
        return $products
    }

    # ---- uninstall mode: interpret msiexec exit codes locally
    $results = @($output | ForEach-Object {
        $r = $_ | Select-Object $columns
        if ($r.Status -eq 'Completed') {
            switch ([int]$r.ExitCode) {
                0       { $r.Status = 'Success';                $r.ErrorCode = '';        $r.Message = 'Uninstalled successfully.' }
                3010    { $r.Status = 'SuccessRebootRequired';  $r.ErrorCode = 'MSI3010'; $r.Message = 'Uninstalled; reboot required.' }
                1641    { $r.Status = 'SuccessRebootInitiated'; $r.ErrorCode = 'MSI1641'; $r.Message = 'Uninstalled; reboot initiated.' }
                1605    { $r.Status = 'NotInstalled';           $r.ErrorCode = 'MSI1605'; $r.Message = 'Product is not installed.' }
                default {
                    $r.Status = 'Failed'
                    $r.ErrorCode = Get-MsiErrorCode ([int]$r.ExitCode)
                    $r.Message = "msiexec exit code {0}: {1}." -f $r.ExitCode, (Get-ErrorInfo $r.ErrorCode).Title
                }
            }
        }
        if ($r.Status -in 'Failed', 'ExecutionError') { Write-CodedMessage -Computer $r.ComputerName -Code $r.ErrorCode -Detail ("{0} {1}" -f $r.DisplayName, $r.Message).Trim() }
        $r
    }) + @($failedRows)

    $results | Sort-Object ComputerName, Msi |
        Format-Table ComputerName, Msi, DisplayName, Version, Status, ExitCode, ErrorCode -AutoSize | Out-Host
    $codes = @($results | ForEach-Object { $_.ErrorCode })
    if (-not (Save-Report -Rows $results -Path $reportFile)) { $codes += 'MRU501' }
    $results | Group-Object Status | Select-Object @{n = 'Status'; e = { $_.Name } }, Count | Format-Table -AutoSize | Out-Host
    Write-CodeSummary -Codes $codes

    $script:ExitCode = if ($connectionFailedServers.Count -eq $servers.Count) { $ExitNoServerReachable }
        elseif ($results | Where-Object { $_.Status -in 'Failed', 'ExecutionError', 'ConnectionFailed' }) { $ExitPartialFailure }
        elseif ($results | Where-Object { $_.Status -in 'SuccessRebootRequired', 'SuccessRebootInitiated' }) { $ExitOkRebootRequired }
        else { $ExitOk }
    return $results
}

$script:ExitCode = $ExitOk
try {
    Invoke-Main
}
catch [MruError] {
    $err = $_.Exception
    $info = Get-ErrorInfo $err.Code
    if (-not $info) { $info = Get-ErrorInfo 'MRU999' }
    if ($err.Code -eq 'MRU502') {
        Write-Host ("WARNING: [{0}] {1}: {2}" -f $err.Code, $info.Title, $err.Detail) -ForegroundColor Yellow
        $script:ExitCode = $ExitAborted
    }
    else {
        Write-Host ("ERROR: [{0}] {1}: {2}" -f $err.Code, $info.Title, $err.Detail) -ForegroundColor Red
        Write-Host ''
        Show-ErrorExplanation -Info $info
        $script:ExitCode = switch ($info.Category) {
            'Input'       { $ExitInvalidInput }
            'Credentials' { $ExitCredentials }
            default       { $ExitPartialFailure }
        }
    }
}
catch {
    $detail = Get-FirstLine $_.Exception.Message
    Write-Host ("ERROR: [MRU999] {0}: {1}" -f (Get-ErrorInfo 'MRU999').Title, $detail) -ForegroundColor Red
    Write-Host ("At: {0}" -f $_.InvocationInfo.PositionMessage)
    Write-Host ''
    Show-ErrorExplanation -Info (Get-ErrorInfo 'MRU999')
    $script:ExitCode = $ExitInternal
}
exit $script:ExitCode
#endregion
