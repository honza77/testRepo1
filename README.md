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
```

Inline parameters and files can be combined; lists are merged and de-duplicated.

| Parameter | Description |
|---|---|
| `-ComputerName` | Server host names |
| `-ComputerListPath` | Text file, one server per line |
| `-MsiName` | MSI display names (wildcards OK) or product code GUIDs |
| `-MsiListPath` | Text file, one MSI name/GUID per line |
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
