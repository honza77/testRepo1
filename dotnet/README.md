# MsiRemoteUninstall (F# / .NET 8)

Console app that works like `Uninstall-RemoteMsi.ps1`: it uninstalls MSI products from a list of
remote Windows servers, or lists and searches the MSIs installed on them. For preparing test servers
it can also **install** MSI packages from a local folder, a UNC share or an HTTP(S) URL. A C# /
.NET Framework 4.8.1 version with the same features is in [`../netfx`](../netfx/README.md).

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

### Install mode (test preparation)

```text
MsiRemoteUninstall -c SRV01 --install "*.msi" -s \\fileserver\msi\test -y
MsiRemoteUninstall -c SRV01 -i Contoso.HelloWorld-1.0.0 -s http://localhost:8080/msi/
MsiRemoteUninstall -c SRV01 -i D:\Packages\App.msi --source-mode direct
MsiRemoteUninstall -c SRV01 -i App.msi -p INSTALLFOLDER=D:\Apps\App
```

- **Where packages come from:** an `-i/--install` entry (or a line in `--install-list`) is a file
  name looked up in `-s/--package-source` (a folder, UNC share or base URL; `.msi` optional;
  wildcards allowed for folders), a full path, or a URL.
- **`--source-mode copy`** (default): the package is copied to `--remote-staging-folder` on each
  server, installed, then deleted (`--keep-staged` keeps it).
  - `psremoting` sends it in chunks through the WinRM session.
  - `wmi` copies it through the `\\server\C$` admin share.
- **`--source-mode direct`:** the server opens the path itself (see `--explain MSI1619` about the
  double hop).
- **Already installed:** packages whose ProductCode (read with msi.dll) is already installed are
  skipped (`AlreadyInstalled`).
- **MSI properties:** `-p/--msi-property NAME=VALUE` passes public MSI properties on install and
  uninstall.

Test packages: [`../samples`](../samples/README.md).

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
- **Help:** `--help`, `--errors [text|markdown|json]` lists all error codes, `--explain <code>`
  explains one code with remediation steps (e.g. `--explain MRU307`, `--explain 1603`).
- **Preflight:** before connecting, the tool resolves the name and tests the TCP port (5985/5986 for
  WinRM, 135 for WMI). `--skip-preflight` disables this.

## Errors and exit codes

Every problem is reported with a code: `MRU1xx` input, `MRU2xx` credentials, `MRU3xx` connection,
`MRU4xx` remote operation, `MRU5xx` local, `MSIxxxx` msiexec exit codes, `MRU999` unexpected.
Codes appear in the console, in the `ErrorCode` column of the CSV report, and in a summary at the end
of the run, which lists the fixes for each code found. All codes, explanations and remediation steps are in
[`../ERROR-CODES.md`](../ERROR-CODES.md).

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | At least one server or MSI failed |
| 2 | Invalid arguments / input (MRU1xx) |
| 3 | Credential problem (MRU2xx) |
| 4 | Aborted at the confirmation question (MRU502) |
| 5 | No server could be reached |
| 6 | Unexpected internal error (MRU999) |
| 10 | Success, but at least one server needs a reboot (MSI3010 / MSI1641) |

The catalog in `Errors.fs` is the single source. After changing it, run `.\Update-ErrorDocs.ps1` to
regenerate `ERROR-CODES.md` and the copy embedded in `Uninstall-RemoteMsi.v2.ps1`. The unit tests
fail if either is out of date, or if the code uses an error code that is not in the catalog.

Exit codes 0, 3010 and 1641 count as success; the last two mean a reboot is required. Code 1618
(another installation running) is retried up to 4 times, 30 s apart.

## Project layout

| File | Purpose |
|---|---|
| `Errors.fs` | Error catalog (codes, explanations, remediation), text/markdown/json rendering |
| `Packages.fs` | Install mode: package resolution (folder / UNC / URL), msi.dll package info |
| `Domain.fs` | Types, list files, product-code / wildcard / regex matching, exit codes |
| `Cli.fs` | Argument parsing and help |
| `Credentials.fs` | Console credential prompt |
| `Transport.fs` | `IRemoteSession` abstraction |
| `Preflight.fs` | DNS + TCP port check before connecting |
| `PsRemotingTransport.fs` | WinRM / PowerShell SDK implementation |
| `WmiTransport.fs` | WMI / DCOM implementation |
| `ErrorClassifier.fs` | Maps WinRM / WMI / COM / .NET exceptions to catalog codes |
| `Report.fs` | Console tables, CSV reports |
| `Program.fs` | Orchestration (parallel per server, retries, confirmation) |
