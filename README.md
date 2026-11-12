# Uninstall-RemoteMsi

PowerShell script that uninstalls MSI products from a list of remote Windows servers.

## Usage

```powershell
# Servers and MSIs from files (prompts for credentials and confirmation)
.\Uninstall-RemoteMsi.ps1 -ComputerListPath .\examples\servers.txt -MsiListPath .\examples\msis.txt

# Inline lists, dry run (shows what would be removed)
.\Uninstall-RemoteMsi.ps1 -ComputerName SRV01,SRV02 -MsiName 'Contoso Agent*' -WhatIf

# Unattended: pre-built credential, skip confirmation, custom report path
$cred = Get-Credential
.\Uninstall-RemoteMsi.ps1 -ComputerListPath .\servers.txt -MsiListPath .\msis.txt `
    -Credential $cred -Confirm:$false -ReportPath C:\Reports\uninstall.csv

# List installed MSIs (nothing is uninstalled), optionally filtered
.\Uninstall-RemoteMsi.ps1 -ComputerListPath .\servers.txt -ListInstalled
.\Uninstall-RemoteMsi.ps1 -ComputerListPath .\servers.txt -ListInstalled -Filter contoso,agent
.\Uninstall-RemoteMsi.ps1 -ComputerListPath .\servers.txt -ListInstalled -FilterMode Wildcard -Filter '*Visual C++ 20??*'
.\Uninstall-RemoteMsi.ps1 -ComputerListPath .\servers.txt -ListInstalled -FilterMode Regex -Filter '^Contoso (Agent|Monitor)'
```

### Listing / searching installed MSIs

`-ListInstalled` connects to the servers and lists the installed MSI products (name, version,
publisher, product code, 32/64-bit) on screen and to `.\MsiInventory_<timestamp>.csv`.
`-Filter` takes one or more patterns; a product is shown if its name, publisher or product code
matches any of them. All modes are case-insensitive. `-FilterMode` picks how patterns are read:

| `-FilterMode` | Meaning | Example |
|---|---|---|
| `Substring` (default) | Plain text, field must contain it | `contoso` |
| `Wildcard` | PowerShell `-like` (`*`, `?`, `[a-z]`); must match the **whole** field | `*Visual C++ 20??*` |
| `Regex` | .NET regular expression, matches anywhere unless anchored | `^Contoso (Agent\|Monitor)` |

Invalid wildcard/regex patterns are rejected before any server is contacted. Use it to find the exact names or
product codes for your MSI list file.

Inline parameters and files can be combined; lists are merged and de-duplicated.

| Parameter | Description |
|---|---|
| `-ComputerName` | Server host names |
| `-ComputerListPath` | Text file, one server per line |
| `-MsiName` | MSI display names (wildcards OK) or product code GUIDs |
| `-MsiListPath` | Text file, one MSI name/GUID per line |
| `-ListInstalled` | List installed MSIs instead of uninstalling |
| `-Filter` | Pattern(s) to search for in list mode (matches name, publisher or product code) |
| `-FilterMode` | `Substring` (default), `Wildcard` or `Regex` |
| `-Credential` | Credentials; prompted if omitted |
| `-UseSSL` | Use WinRM over HTTPS |
| `-ThrottleLimit` | Parallel servers (default 16) |
| `-ReportPath` | CSV report path (default `.\MsiUninstallReport_<timestamp>.csv`) |
| `-RemoteLogFolder` | msiexec log folder on servers (default `C:\Windows\Temp\MsiUninstallLogs`) |
| `-WhatIf` / `-Confirm:$false` | Dry run / skip the confirmation prompt |

In list files, blank lines and lines starting with `#` are ignored.

## Requirements

- PowerShell Remoting (WinRM) enabled on the targets (`Enable-PSRemoting`).
- The account must be a local administrator on the targets.

## How it works

Installed MSI products are found via the registry Uninstall keys (64- and 32-bit),
not `Win32_Product` (which triggers an MSI repair of every product). Each match is removed with
`msiexec /x {ProductCode} /qn /norestart /l*v <log>`. Exit codes 0, 3010 and 1641 count as success
(the last two mean a reboot is needed); 1618 (another install running) is retried up to 5 times.
