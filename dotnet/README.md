# MsiRemoteUninstall (F# / .NET 8)

Console app that works like `Uninstall-RemoteMsi.ps1`: it uninstalls MSI products from a list of
remote Windows servers, or lists and searches the MSIs installed on them.

## Build

Requires the .NET 8 SDK. The tool itself runs on **Windows**, because it uses the Windows WinRM client and DCOM.

```powershell
cd dotnet
dotnet test                                   # unit tests (run on any OS)
dotnet publish MsiRemoteUninstall -c Release -r win-x64 --self-contained false -o publish
.\publish\MsiRemoteUninstall.exe --help
```

Use `--self-contained true` if the machine running it has no .NET 8 runtime.

## Transports (`-t, --transport`)

| Transport | How it works | Ports | Server requirements |
|---|---|---|---|
| `psremoting` (default) | PowerShell Remoting hosted in-process through the PowerShell SDK (same as `Invoke-Command`). The same PowerShell code as the `.ps1` script runs remotely. | 5985 HTTP, or 5986 HTTPS with `--ssl` | WinRM enabled (`Enable-PSRemoting`), plus an HTTPS listener for `--ssl` |
| `wmi` | WMI over DCOM through `System.Management`. Products are read with `StdRegProv`. msiexec is started with `Win32_Process.Create`, and its exit code is passed back through a temporary registry value. | 135 plus dynamic RPC ports | WMI firewall rules enabled. **No PowerShell or WinRM needed.** |

`psremoting` only:

- `--ssl`: use HTTPS.
- `--port`: use a different WinRM port.
- `--auth default|negotiate|kerberos|credssp|basic`: choose the authentication method.
- `--skip-cert-check`: accept self-signed certificates with `--ssl`.

For both transports the account must be a local administrator on the servers.

## Usage

```text
# Uninstall: servers and MSIs from files, console credential prompt, y/N confirmation
MsiRemoteUninstall --computer-list servers.txt --msi-list msis.txt

# Dry run
MsiRemoteUninstall -c SRV01,SRV02 -m "Contoso Agent*" --what-if

# WinRM over HTTPS, no confirmation
MsiRemoteUninstall --computer-list servers.txt --msi-list msis.txt --ssl -y -u CONTOSO\admin

# WMI/DCOM, use the current Windows logon
MsiRemoteUninstall --computer-list servers.txt -m {12345678-90AB-CDEF-1234-567890ABCDEF} -t wmi --current-user

# List / search installed MSIs
MsiRemoteUninstall -c SRV01 --list
MsiRemoteUninstall -c SRV01 --list -f contoso -f agent
MsiRemoteUninstall -c SRV01 --list --filter-mode wildcard -f "*Visual C++ 20??*"
MsiRemoteUninstall -c SRV01 --list --filter-mode regex -f "^Contoso (Agent|Monitor)"
```

Run `MsiRemoteUninstall --help` for every option. In brief:

- **Servers / MSIs:** `-c` and `--computer-list`, `-m` and `--msi-list`. Each can be repeated and all are combined.
  List files use the same format as the PowerShell version (`#` comments, blank lines ignored).
- **MSI entries:** a product code GUID, or a display name. Wildcards are allowed in names, and a name
  without wildcards must match exactly.
- **List mode:** `--list`, `-f/--filter` (repeatable, OR), `--filter-mode substring|wildcard|regex`.
  Filters are checked against name, publisher and product code. Invalid patterns are rejected
  before anything connects.
- **Credentials:** prompted in the console (password masked, kept in a `SecureString`).
  `-u` fills in the user name. `--password-env VAR` reads the password from an environment variable
  for unattended runs. `--current-user` uses your Windows logon.
- **Behaviour:**
  - `--what-if` shows what would be removed without removing it. `-y` skips the confirmation.
  - `--throttle` (default 16) sets how many servers are handled at once.
  - `--timeout` (minutes per msiexec run, default 60).
  - `--report file.csv` sets the report path. `--remote-log-folder` sets where msiexec logs go on the servers.
- **Exit codes:** `0` everything OK, `1` at least one failure, `2` invalid arguments.

Exit codes 0, 3010 and 1641 count as success; the last two mean a reboot is required. Code 1618
(another installation running) is retried up to 4 times, 30 s apart.

## Project layout

| File | Purpose |
|---|---|
| `Domain.fs` | Types, list files, product-code / wildcard / regex matching, exit codes |
| `Cli.fs` | Argument parsing and help |
| `Credentials.fs` | Console credential prompt |
| `Transport.fs` | `IRemoteSession` abstraction |
| `PsRemotingTransport.fs` | WinRM / PowerShell SDK implementation |
| `WmiTransport.fs` | WMI / DCOM implementation |
| `Report.fs` | Console tables, CSV reports |
| `Program.fs` | Orchestration (parallel per server, retries, confirmation) |
