/// Error catalog: every error / warning the tool can report has a stable code,
/// a plain-language explanation and a list of remediation steps.
///
/// This catalog is the single source of truth. ERROR-CODES.md and the catalog
/// embedded in Uninstall-RemoteMsi.v2.ps1 are generated from it
/// (MsiRemoteUninstall --errors markdown / --errors json).
module MsiRemoteUninstall.Errors

open System
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
type Severity =
    | Error
    | Warning
    | Info

[<RequireQualifiedAccess>]
type Category =
    | Input
    | Credentials
    | Connection
    | RemoteOperation
    | Msi
    | Local
    | Internal

type ErrorInfo =
    { Code: string
      Category: Category
      Severity: Severity
      Title: string
      AppliesTo: string
      Explanation: string
      Remediation: string list }

let private e code cat sev title applies expl rem =
    { Code = code
      Category = cat
      Severity = sev
      Title = title
      AppliesTo = applies
      Explanation = expl
      Remediation = rem }

let catalog: ErrorInfo list =
    [
      // ------------------------------------------------------------ 1xx input
      e "MRU101" Category.Input Severity.Error "Invalid command-line argument" ".NET app"
          "An argument was not recognised, is missing its value, or its value is outside the allowed range. (In the PowerShell script, PowerShell itself validates parameters before the script starts.)"
          [ "Compare the spelling with the help: MsiRemoteUninstall --help."
            "Put values that contain spaces or commas in quotes, e.g. -m \"Contoso Agent*\"."
            "Numeric ranges: --port 1-65535, --throttle 1-256, --timeout 1-1440 (minutes)." ]
      e "MRU102" Category.Input Severity.Error "No servers specified" "All"
          "The list of target servers is empty, so there is nothing to do."
          [ "Pass servers with --computer / -ComputerName, or a file with --computer-list / -ComputerListPath."
            "If you used a list file, make sure it is not empty and not completely commented out (lines starting with # are ignored)." ]
      e "MRU103" Category.Input Severity.Error "No MSIs specified" "All"
          "Uninstall mode needs at least one MSI display name or product code."
          [ "Pass MSIs with --msi / -MsiName, or a file with --msi-list / -MsiListPath."
            "If you only want to see what is installed, use list mode: --list / -ListInstalled." ]
      e "MRU104" Category.Input Severity.Error "List file not found" "All"
          "A server or MSI list file does not exist at the given path."
          [ "Check the path and file name for typos."
            "Relative paths are resolved against the current directory - use a full path if unsure."
            "Quote paths that contain spaces." ]
      e "MRU105" Category.Input Severity.Error "List file cannot be read" "All"
          "The list file exists but could not be opened or read."
          [ "Check that your account has read permission on the file."
            "Close the file if another program (e.g. Excel) holds it locked."
            "Save the file as plain text (UTF-8 or ANSI), one entry per line." ]
      e "MRU106" Category.Input Severity.Error "Invalid search filter pattern" "All"
          "A --filter / -Filter pattern is not valid for the chosen filter mode (regex or wildcard). Nothing was contacted."
          [ "Regex mode: escape special characters with a backslash, e.g. C\\+\\+ or \\(x64\\), and balance all brackets and parentheses."
            "Wildcard mode: close every '[' with ']'; to match a literal '[' or '*' escape it with a backtick, e.g. `[."
            "If you just want 'contains this text', use the default substring mode (no special characters at all)." ]
      e "MRU107" Category.Input Severity.Error "Invalid MSI name pattern" "All"
          "An MSI entry that is not a product code is treated as a wildcard pattern, and this one is not valid (usually an unclosed '[')."
          [ "Close every '[' with ']' or escape it with a backtick: `[."
            "Alternatively use the product code GUID, which is matched exactly. List mode (--list / -ListInstalled) shows the product codes." ]
      e "MRU108" Category.Input Severity.Warning "Option ignored" "All"
          "An option was given that has no effect in this combination (for example --filter without --list, or --ssl with the WMI transport). The run continues without it."
          [ "Remove the option, or add the option it depends on (see the warning text)." ]

      // ------------------------------------------------------------ 2xx credentials
      e "MRU201" Category.Credentials Severity.Error "No user name entered / credential prompt cancelled" "All"
          "The credential prompt was cancelled or the user name was left empty."
          [ "Run again and enter the user name as DOMAIN\\user, user@domain.fqdn, or SERVER\\user for a local account."
            "Pre-fill the user name with --user / -UserName."
            "To use your current Windows logon instead, use --current-user (.NET) or run the script from a session already logged on as the right account and pass -Credential (Get-Credential)." ]
      e "MRU202" Category.Credentials Severity.Error "No password entered" "All"
          "The password was empty. Empty passwords cannot be used for remote management."
          [ "Run again and type the password (it is not shown while typing)."
            "For unattended runs use --password-env VAR (.NET) or pass a prepared -Credential object (PowerShell)." ]
      e "MRU203" Category.Credentials Severity.Error "Password environment variable missing" ".NET app"
          "--password-env was given, but that environment variable does not exist or is empty in this process."
          [ "Set the variable in the same shell before starting the tool, e.g. PowerShell: $env:MRU_PW = '...'; cmd: set MRU_PW=..."
            "Check the variable name passed to --password-env (no $ or % around it)." ]
      e "MRU204" Category.Credentials Severity.Error "Cannot prompt for credentials" "All"
          "There is no interactive console to ask for the password (for example when started from a scheduled task, a service or a non-interactive remote shell)."
          [ "Run the tool from an interactive console."
            ".NET: pass --user together with --password-env, or use --current-user."
            "PowerShell: pass -Credential with a PSCredential object, or use -ConsoleCredential in a console host." ]

      // ------------------------------------------------------------ 3xx connection
      e "MRU301" Category.Connection Severity.Error "Server name cannot be resolved" "All"
          "DNS (or NetBIOS) could not translate the server name into an IP address, so no connection was attempted."
          [ "Check the server name for typos."
            "Try the fully qualified name, e.g. srv01.contoso.local."
            "Test name resolution: Resolve-DnsName srv01 (PowerShell) or nslookup srv01."
            "Check the client's DNS servers / VPN connection, or that the server still exists." ]
      e "MRU302" Category.Connection Severity.Error "Server unreachable or management port closed" "All"
          "The name resolved, but a TCP connection to the remote management port was refused or timed out (WinRM: 5985 HTTP / 5986 HTTPS, WMI/DCOM: 135). The server is offline, a firewall blocks the port, or nothing is listening on it."
          [ "Check that the server is running: Test-Connection srv01."
            "Test the port: Test-NetConnection srv01 -Port 5985 (or 5986 with SSL, 135 for WMI)."
            "WinRM: on the server run Enable-PSRemoting -Force, which also opens the 'Windows Remote Management (HTTP-In)' firewall rule. For HTTPS an HTTPS listener and a 5986 firewall rule are needed."
            "WMI: enable the firewall rule group 'Windows Management Instrumentation (WMI)' on the server."
            "Check network firewalls / security groups between client and server."
            "If the port check itself is the problem (e.g. through a proxy), skip it with --skip-preflight / -SkipPreflight." ]
      e "MRU303" Category.Connection Severity.Error "Access denied" "All"
          "The server was reached and the credentials were checked, but the account is not allowed to do this. Uninstalling software requires local administrator rights on the server."
          [ "Make sure the account is a member of the local Administrators group on the server (directly or via a domain group)."
            "Local (non-domain) administrator accounts are filtered by UAC remote restrictions: use a domain account, or set HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\\LocalAccountTokenFilterPolicy = 1 (DWORD) on the server."
            "WinRM: check the endpoint permissions on the server: Get-PSSessionConfiguration Microsoft.PowerShell | Select Permission."
            "WMI: check namespace security (wmimgmt.msc -> WMI Control -> Security, root\\cimv2 and root\\default need 'Remote Enable') and DCOM remote launch/activation permissions (dcomcnfg)."
            "Verify the credentials are the ones you intended (--user / -UserName)." ]
      e "MRU304" Category.Connection Severity.Error "Logon failure" "All"
          "The server rejected the user name or password: wrong password, unknown user, locked or disabled account, expired password, or the domain controller could not be contacted."
          [ "Re-enter the password carefully (Caps Lock, keyboard layout)."
            "Use the form DOMAIN\\user or user@domain.fqdn; for a local account on the server use SERVER\\user."
            "Check whether the account is locked out, disabled or its password expired (Get-ADUser user -Properties LockedOut,Enabled,PasswordExpired)."
            "Kerberos requires the clocks of client, server and domain controller to be within 5 minutes - check time synchronisation (w32tm /query /status)." ]
      e "MRU305" Category.Connection Severity.Error "WinRM service not available" "PowerShell Remoting"
          "The WinRM port answers, but WinRM did not accept the request: the service is stopped, there is no listener for this protocol (HTTP vs HTTPS) or port, the listener is bound to another address, or PowerShell remoting endpoints are disabled."
          [ "On the server: Enable-PSRemoting -Force (or winrm quickconfig)."
            "Check the service: Get-Service WinRM (should be Running, startup Automatic)."
            "List listeners: winrm enumerate winrm/config/listener - for --ssl / -UseSSL an HTTPS listener is required."
            "Check the port option (--port / -Port) matches the listener."
            "From the client: Test-WSMan srv01 (add -UseSSL for HTTPS)."
            "Alternatively use the WMI transport (.NET: -t wmi)." ]
      e "MRU306" Category.Connection Severity.Error "TLS / certificate problem" "PowerShell Remoting with SSL"
          "The HTTPS connection to WinRM failed because the server certificate is not trusted, has expired, or its name does not match the server name you used."
          [ "Connect with the exact name in the certificate's Subject / SAN - usually the FQDN (srv01.contoso.local)."
            "Make sure the issuing CA is trusted on the client (Local Computer\\Trusted Root Certification Authorities)."
            "Renew the certificate if it has expired and update the HTTPS listener's thumbprint."
            "For testing only, skip the checks with --skip-cert-check / -SkipCertificateCheck (traffic is still encrypted, but the server is not verified)." ]
      e "MRU307" Category.Connection Severity.Error "Authentication method could not be used" "All"
          "The client and server could not agree on an authentication method. Typical causes: connecting by IP address or alias (Kerberos needs the real host name), the client or server is not in the domain and the server is not in TrustedHosts, CredSSP is not enabled, or Basic authentication is used over plain HTTP."
          [ "Connect using the server's real host name or FQDN, not an IP address or DNS alias."
            "For workgroup servers or IP addresses add the server to TrustedHosts on the client (as admin): Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value 'srv01' -Concatenate - or use HTTPS (--ssl / -UseSSL), which does not need TrustedHosts."
            "CredSSP: enable it on both sides (Enable-WSManCredSSP -Role Client / -Role Server)."
            "Basic: only works with local accounts and requires HTTPS or AllowUnencrypted on the server - prefer the default (Negotiate)."
            "Check the server's SPNs if Kerberos fails for a correct name: setspn -L srv01." ]
      e "MRU308" Category.Connection Severity.Error "RPC server unavailable (WMI / DCOM)" "WMI transport"
          "DCOM could not reach the WMI service. Port 135 may be open, but DCOM also needs the dynamic RPC port range (49152-65535 by default), or the WMI service is not running."
          [ "On the server enable the firewall rule group 'Windows Management Instrumentation (WMI)' (it covers the dynamic ports for WMI)."
            "Allow the dynamic RPC ports on network firewalls between client and server."
            "Check the WMI service on the server: Get-Service Winmgmt."
            "Or use the PowerShell Remoting transport (one fixed port, 5985/5986)." ]
      e "MRU309" Category.Connection Severity.Error "WMI error on the server" "WMI transport"
          "WMI on the server reported a problem with a namespace, class or provider (root\\cimv2 or root\\default / StdRegProv). The WMI repository may be damaged."
          [ "On the server check the repository: winmgmt /verifyrepository (and winmgmt /salvagerepository if inconsistent)."
            "Restart the WMI service: Restart-Service Winmgmt -Force."
            "Test locally on the server: Get-CimInstance Win32_OperatingSystem."
            "Use the PowerShell Remoting transport as a workaround." ]
      e "MRU310" Category.Connection Severity.Error "Connection timed out" "All"
          "The server accepted the network connection but did not complete the session setup in time. The server may be overloaded, hanging, or the network is very slow."
          [ "Retry later - check the server's CPU / memory load."
            "Test the connection directly: Test-WSMan srv01 or Get-CimInstance -ComputerName srv01 Win32_OperatingSystem."
            "Reduce the parallelism with --throttle / -ThrottleLimit if many servers share a slow link." ]
      e "MRU399" Category.Connection Severity.Error "Other connection error" "All"
          "Connecting failed for a reason that does not fit the other categories. The detail message contains the original error from Windows."
          [ "Read the detail message in the report - it usually names the cause."
            "Test the connection manually: Test-WSMan srv01, Enter-PSSession srv01, or Get-CimInstance -ComputerName srv01 Win32_OperatingSystem."
            "On the server check the event logs Microsoft-Windows-WinRM/Operational and System." ]

      // ------------------------------------------------------------ 4xx remote operation
      e "MRU401" Category.RemoteOperation Severity.Error "Could not read installed products" "All"
          "The connection worked, but reading the list of installed MSI products from the server's registry (Uninstall keys) failed."
          [ "Make sure the account is a local administrator on the server."
            "Retry - a transient WMI / WinRM problem is possible."
            "Test on the server: Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'."
            "WMI transport: see MRU309 for WMI repository problems." ]
      e "MRU402" Category.RemoteOperation Severity.Error "Could not start msiexec on the server" "All"
          "The uninstall process could not be started. WMI Win32_Process.Create codes: 2 = access denied, 3 = insufficient privilege, 8 = unknown failure, 9 = path not found, 21 = invalid parameter. It can also mean the remote log folder could not be created."
          [ "Make sure the account is a local administrator on the server."
            "Check that the remote log folder (--remote-log-folder / -RemoteLogFolder) is a valid local path on the server and its drive exists."
            "Check whether AppLocker / WDAC / antivirus blocks msiexec.exe, cmd.exe or reg.exe for remote processes."
            "Verify %SystemRoot%\\System32\\msiexec.exe exists on the server." ]
      e "MRU403" Category.RemoteOperation Severity.Error "msiexec did not finish in time" "All"
          "msiexec was still running when the timeout expired. It may be waiting for a dialog that cannot be shown (a custom action ignoring /qn), blocked by a locked file or service, or the product is simply very large. The process is left running on the server."
          [ "Look at the msiexec log on the server (path in the report) - the last lines show what it is doing."
            "Check for running installer processes on the server: Get-Process msiexec."
            "Stop the hung msiexec process if needed, or reboot the server to clear pending operations."
            "Increase the timeout: --timeout / -TimeoutMinutes (minutes)." ]
      e "MRU404" Category.RemoteOperation Severity.Error "msiexec result could not be determined" "All"
          "msiexec ran but its exit code could not be read back. WMI transport: the temporary value under HKLM\\SOFTWARE\\MsiRemoteUninstall\\ExitCodes was not written (cmd.exe or reg.exe blocked, or the process was killed). PowerShell Remoting: the remote command returned no exit code."
          [ "Check whether the product is still installed: run in list mode (--list / -ListInstalled)."
            "Read the msiexec log on the server (path in the report)."
            "Check AppLocker / WDAC / antivirus rules for cmd.exe and reg.exe." ]
      e "MRU405" Category.RemoteOperation Severity.Error "Remote command failed" "All"
          "An unexpected error occurred while running the command on the server, or the session broke (server rebooted, network dropped, WinRM memory or idle limits reached)."
          [ "Read the detail message in the report."
            "Run again - products that were already removed are reported as NotFound, so re-running is safe."
            "Check the server's event logs (Application, System, Microsoft-Windows-WinRM/Operational)."
            "If the session broke, check whether the server rebooted (some uninstalls force a reboot)." ]

      // ------------------------------------------------------------ 5xx local
      e "MRU501" Category.Local Severity.Warning "Report could not be written" "All"
          "The CSV report could not be saved. The results were still shown on screen and (PowerShell) returned as objects."
          [ "Check that the target folder exists and you can write to it."
            "Close the file if it is open in Excel."
            "Choose another path with --report / -ReportPath." ]
      e "MRU502" Category.Local Severity.Info "Aborted by user" "All"
          "The confirmation question was answered with No, so nothing was uninstalled."
          [ "Run again and confirm with 'y', or skip the question with -y / --yes (.NET) or -Confirm:$false (PowerShell)."
            "Use --what-if / -WhatIf first to see what would be removed." ]
      e "MRU503" Category.Local Severity.Error "Remote management not available on this OS" ".NET app"
          "The WinRM client and WMI/DCOM APIs used by the tool exist only on Windows. This happens when the .NET tool is started on Linux or macOS."
          [ "Run the tool on Windows 10/11 or Windows Server 2016 or newer."
            "Or use the PowerShell script from a Windows machine." ]

      // ------------------------------------------------------------ MSI exit codes
      e "MSI1601" Category.Msi Severity.Error "Windows Installer service not accessible" "All"
          "msiexec could not contact the Windows Installer service on the server. The service may be disabled or its registration damaged, or Windows is in Safe Mode."
          [ "On the server: Get-Service msiserver - the startup type must not be Disabled."
            "Re-register Windows Installer (as admin): msiexec /unregister then msiexec /regserver."
            "Reboot the server and retry." ]
      e "MSI1602" Category.Msi Severity.Error "Uninstall cancelled" "All"
          "The uninstall was cancelled. With a silent uninstall (/qn) this usually means a custom action of the product cancelled it."
          [ "Read the msiexec log on the server (path in the report) for the reason."
            "Check the vendor's documentation for prerequisites for uninstalling (e.g. a removal password or a service that must be stopped)." ]
      e "MSI1603" Category.Msi Severity.Error "Fatal error during uninstall" "All"
          "A generic failure inside the uninstall. The most common causes: a custom action of the product failed, files or services are in use, a pending reboot, or missing permissions inside the product's own uninstall logic."
          [ "Open the verbose msiexec log (path in the report) and search for 'Return value 3' - the lines just above it name the failing action."
            "Stop the product's processes and services, then retry."
            "Reboot the server (clears pending file operations) and retry."
            "Use the vendor's removal / cleanup tool if the product's uninstaller is broken." ]
      e "MSI1605" Category.Msi Severity.Info "Product is not installed" "All"
          "msiexec reports the product is not installed. It was removed in the meantime (for example by an earlier entry in the same run), or it was installed per-user under another account."
          [ "Usually nothing to do. Check with list mode (--list / -ListInstalled) if unsure." ]
      e "MSI1612" Category.Msi Severity.Error "Installation source unavailable" "All"
          "Windows Installer needs the original MSI package to uninstall, but its cached copy (C:\\Windows\\Installer) and the original source location are missing."
          [ "Copy the exact same version of the original .msi back to its original source path, or run msiexec /x <path-to-product.msi> on the server."
            "Use the vendor's cleanup tool, or Microsoft's 'Program Install and Uninstall troubleshooter'."
            "Avoid cleaning C:\\Windows\\Installer manually in future." ]
      e "MSI1618" Category.Msi Severity.Error "Another installation is in progress" "All"
          "Only one Windows Installer operation can run at a time. Another installation (Windows Update, software deployment, a hung msiexec) was still running after all retries."
          [ "Wait until the other installation finishes and run again."
            "Check on the server: Get-Process msiexec, and Windows Update / deployment agent activity."
            "If an msiexec process is hung, stop it or reboot the server." ]
      e "MSI1619" Category.Msi Severity.Error "Installation package could not be opened" "All"
          "The cached MSI package of the product could not be opened - it is missing, inaccessible or corrupt."
          [ "Same as MSI1612: restore the original .msi of the same version, or uninstall using the package path."
            "Check antivirus quarantine for C:\\Windows\\Installer\\*.msi files." ]
      e "MSI1620" Category.Msi Severity.Error "Installation package is invalid" "All"
          "The cached MSI package exists but is not a valid Windows Installer package (corrupt)."
          [ "Restore the original .msi of the same version and uninstall using its path, or use the vendor's cleanup tool." ]
      e "MSI1622" Category.Msi Severity.Error "Log file could not be opened" "All"
          "msiexec could not create the verbose log file in the remote log folder."
          [ "Check that the remote log folder (--remote-log-folder / -RemoteLogFolder) is a valid, writable local path on the server."
            "Check free disk space on the server."
            "Avoid very long paths or special characters in the log folder path." ]
      e "MSI1624" Category.Msi Severity.Error "Error applying transforms" "All"
          "The product was installed with a transform (.mst) whose cached copy is missing or invalid, so Windows Installer cannot open the product configuration."
          [ "Restore the transform file to the path used at install time."
            "Use the vendor's cleanup tool." ]
      e "MSI1625" Category.Msi Severity.Error "Prohibited by system policy" "All"
          "A Group Policy or local policy prevents this uninstall (for example 'Turn off Windows Installer' or 'Prohibit removal of updates')."
          [ "Check the applied policies on the server: gpresult /h report.html."
            "Ask the policy owner for an exception, or run the uninstall with an account the policy allows." ]
      e "MSI1633" Category.Msi Severity.Error "Platform not supported" "All"
          "The package is not supported on this processor architecture / platform."
          [ "Check the msiexec log; this is unusual for an uninstall and may indicate a corrupt product registration."
            "Use the vendor's cleanup tool." ]
      e "MSI1639" Category.Msi Severity.Error "Invalid command line argument" "All"
          "msiexec rejected its command line."
          [ "Check the remote log folder path (--remote-log-folder / -RemoteLogFolder) for unusual characters such as quotes."
            "If the problem persists, report it as a bug including the product code." ]
      e "MSI1641" Category.Msi Severity.Warning "Uninstalled - reboot initiated" "All"
          "The uninstall succeeded and the installer started a reboot of the server, despite /norestart (some products force it)."
          [ "Nothing to do - the server is restarting. Check it comes back online." ]
      e "MSI3010" Category.Msi Severity.Warning "Uninstalled - reboot required" "All"
          "The uninstall succeeded, but some files or settings are only removed after the server restarts."
          [ "Schedule a reboot of the server, e.g. Restart-Computer -ComputerName srv01 at a convenient time." ]
      e "MSI-OTHER" Category.Msi Severity.Error "Other msiexec error" "All"
          "msiexec returned an exit code that is not described here."
          [ "Look up the code: net helpmsg <code>, or search Microsoft's 'MsiExec.exe and InstMsi.exe error messages' page."
            "Read the verbose msiexec log on the server (path in the report)." ]

      // ------------------------------------------------------------ internal
      e "MRU999" Category.Internal Severity.Error "Unexpected internal error" "All"
          "An error occurred that the tool does not handle specifically - most likely a bug."
          [ "Read the detail message, it contains the original error."
            "Run again; if it happens again, report it together with the detail message and the command line (without passwords)." ] ]

/// Process exit codes (same for the .NET app and the PowerShell script).
let exitCodes: (int * string * string) list =
    [ 0, "Success", "Everything completed successfully."
      1, "Partial failure", "At least one server or MSI failed - see the report and the listed error codes."
      2, "Invalid input", "Invalid arguments or input files (MRU1xx). Nothing was contacted."
      3, "Credential problem", "No usable credentials (MRU2xx). Nothing was contacted."
      4, "Aborted", "The confirmation question was answered with No (MRU502)."
      5, "No server reachable", "Every server failed to connect (MRU3xx)."
      6, "Internal error", "Unexpected error (MRU999)."
      10, "Success, reboot required", "Everything succeeded, but at least one server needs a reboot (MSI3010 / MSI1641)." ]

let private byCode =
    catalog |> List.map (fun e -> e.Code, e) |> dict

/// Finds an entry; accepts any case, and bare msiexec numbers ("1603" -> MSI1603).
let tryFind (code: string) : ErrorInfo option =
    if isNull code then
        None
    else
        let c = code.Trim().ToUpperInvariant()
        let c = if c <> "" && c |> Seq.forall Char.IsDigit then "MSI" + c else c
        match byCode.TryGetValue c with
        | true, e -> Some e
        | _ -> None

let get (code: string) : ErrorInfo =
    match tryFind code with
    | Some e -> e
    | None -> byCode.["MRU999"]

/// Catalog code for an msiexec exit code.
let msiCode (exitCode: int) =
    let c = sprintf "MSI%d" exitCode
    if byCode.ContainsKey c then c else "MSI-OTHER"

/// Exception carrying a catalog code plus a detail message.
exception MruException of Code: string * Detail: string with
    override this.Message =
        let info = get this.Code
        if String.IsNullOrWhiteSpace this.Detail then
            sprintf "[%s] %s" this.Code info.Title
        else
            sprintf "[%s] %s: %s" this.Code info.Title this.Detail

let raiseMru code detail : 'T = raise (MruException(code, detail))

/// First non-empty line of an exception message (Windows messages are often multi-line).
let firstLine (s: string) =
    if String.IsNullOrWhiteSpace s then
        ""
    else
        s.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun l -> l.Trim())
        |> Array.filter ((<>) "")
        |> fun a -> if a.Length = 0 then "" else String.Join(" ", a)

// ------------------------------------------------------------------ rendering

let private wrap (indent: string) (width: int) (text: string) =
    let sb = StringBuilder()
    let mutable line = indent
    for word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries) do
        if line.Length + word.Length + 1 > width && line.Trim() <> "" then
            sb.AppendLine(line.TrimEnd()) |> ignore
            line <- String(' ', indent.Length)
        line <- line + (if line.EndsWith " " then "" else " ") + word
    sb.Append(line.TrimEnd()).ToString()

let private categoryName =
    function
    | Category.Input -> "Input"
    | Category.Credentials -> "Credentials"
    | Category.Connection -> "Connection"
    | Category.RemoteOperation -> "Remote operation"
    | Category.Msi -> "msiexec exit code"
    | Category.Local -> "Local"
    | Category.Internal -> "Internal"

/// Full console explanation of one code.
let explainText (e: ErrorInfo) =
    let sb = StringBuilder()
    sb.AppendLine(sprintf "%s  %s" e.Code e.Title) |> ignore
    sb.AppendLine(sprintf "Category: %s   Severity: %A   Applies to: %s" (categoryName e.Category) e.Severity e.AppliesTo) |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("What it means:") |> ignore
    sb.AppendLine(wrap "  " 100 e.Explanation) |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("How to fix it:") |> ignore
    e.Remediation |> List.iteri (fun i r -> sb.AppendLine(wrap (sprintf "  %d. " (i + 1)) 100 r) |> ignore)
    sb.ToString()

/// Short list of all codes.
let listText () =
    let sb = StringBuilder()
    sb.AppendLine("Error codes (use --explain <code> for details and remediation):") |> ignore
    for group in catalog |> List.groupBy (fun e -> e.Category) do
        sb.AppendLine() |> ignore
        sb.AppendLine(sprintf "%s" (categoryName (fst group))) |> ignore
        for e in snd group do
            sb.AppendLine(sprintf "  %-10s %-8s %s" e.Code (sprintf "%A" e.Severity) e.Title) |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("Process exit codes:") |> ignore
    for (c, t, d) in exitCodes do
        sb.AppendLine(sprintf "  %-3d %-25s %s" c t d) |> ignore
    sb.ToString()

/// Markdown document (ERROR-CODES.md is generated with this).
let markdown () =
    let sb = StringBuilder()
    let line (s: string) = sb.AppendLine s |> ignore
    let anchor (code: string) = code.ToLowerInvariant()
    line "# Error codes"
    line ""
    line "<!-- Generated by: MsiRemoteUninstall --errors markdown. Do not edit by hand; edit dotnet/MsiRemoteUninstall/Errors.fs. -->"
    line ""
    line "These codes are used by both the .NET tool (`MsiRemoteUninstall`) and `Uninstall-RemoteMsi.v2.ps1`."
    line "They appear in the console output and in the `ErrorCode` column of the CSV report."
    line ""
    line "Show the explanation of a code from the command line:"
    line ""
    line "```powershell"
    line "MsiRemoteUninstall --explain MRU303          # .NET"
    line ".\\Uninstall-RemoteMsi.v2.ps1 -ExplainError MRU303   # PowerShell"
    line "MsiRemoteUninstall --errors                  # list all codes"
    line ".\\Uninstall-RemoteMsi.v2.ps1 -ListErrorCodes"
    line "```"
    line ""
    line "## Process exit codes"
    line ""
    line "| Exit code | Meaning | Description |"
    line "|---|---|---|"
    for (c, t, d) in exitCodes do
        line (sprintf "| %d | %s | %s |" c t d)
    line ""
    line "## Index"
    line ""
    line "| Code | Severity | Title | Applies to |"
    line "|---|---|---|---|"
    for e in catalog do
        line (sprintf "| [%s](#%s) | %A | %s | %s |" e.Code (anchor e.Code) e.Severity e.Title e.AppliesTo)
    for group in catalog |> List.groupBy (fun e -> e.Category) do
        line ""
        line (sprintf "## %s" (categoryName (fst group)))
        for e in snd group do
            line ""
            line (sprintf "### %s" e.Code)
            line ""
            line (sprintf "**%s** - severity: %A, applies to: %s" e.Title e.Severity e.AppliesTo)
            line ""
            line e.Explanation
            line ""
            line "How to fix it:"
            line ""
            e.Remediation |> List.iteri (fun i r -> line (sprintf "%d. %s" (i + 1) r))
    sb.ToString()

/// JSON export (embedded into Uninstall-RemoteMsi.v2.ps1).
let json () =
    let items =
        catalog
        |> List.map (fun e ->
            {| Code = e.Code
               Category = categoryName e.Category
               Severity = sprintf "%A" e.Severity
               Title = e.Title
               AppliesTo = e.AppliesTo
               Explanation = e.Explanation
               Remediation = e.Remediation |})
    let exits = exitCodes |> List.map (fun (c, t, d) -> {| ExitCode = c; Title = t; Description = d |})
    JsonSerializer.Serialize(
        {| ErrorCodes = items; ExitCodes = exits |},
        JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
    )
