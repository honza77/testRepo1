# MsiRemoteUninstall (C# / .NET Framework 4.8.1)

A C# port of the F# tool in [`../dotnet`](../dotnet/README.md) as a separate solution. It has the same
options, error codes, exit codes and output. It targets **.NET Framework 4.8.1**, so it runs on any
current Windows / Windows Server without installing a .NET runtime.

It does three things:
- **Uninstall** MSI products on remote servers (`-m`, `--msi-list`).
- **List / search** installed MSI products (`--list`, `--filter`, `--filter-mode`).
- **Install** MSI packages from a local folder, a UNC share or an HTTP(S) URL (`--install`).
  This is meant for preparing test servers; see [`../samples`](../samples/README.md).

## Build

```powershell
cd netfx
dotnet build MsiRemoteUninstall.NetFx.sln -c Release
dotnet test  MsiRemoteUninstall.NetFx.sln            # unit tests (run on .NET 8, any OS)
.\src\MsiRemoteUninstall\bin\Release\net481\MsiRemoteUninstall.exe --help
```

You can also open `MsiRemoteUninstall.NetFx.sln` in Visual Studio 2022. Copy the whole
`bin\Release\net481` folder to wherever you run the tool.

## Projects

| Project | Target | Content |
|---|---|---|
| `src/MsiRemoteUninstall.Core` | net481 + net8.0 | Error catalog, CLI parsing, matching, package resolution (folder / UNC / URL, msi.dll), preflight, console / CSV reports. No remoting code. |
| `src/MsiRemoteUninstall` | net481 (exe) | `PsRemotingSession` (Windows PowerShell 5.1 engine, WinRM HTTP/HTTPS), `WmiSession` (System.Management over DCOM, admin-share upload), `ErrorClassifier`, `Operations`, `Program` |
| `tests/MsiRemoteUninstall.Core.Tests` | net8.0 | xUnit tests for the core library |

## Transports

| `-t` | How | Server requirements |
|---|---|---|
| `psremoting` (default) | WinRM through `System.Management.Automation` from the Windows PowerShell 5.1 GAC (compile-time reference only: `Microsoft.PowerShell.5.1.ReferenceAssemblies`). `--ssl`, `--port`, `--auth`, `--skip-cert-check`. | `Enable-PSRemoting` |
| `wmi` | WMI over DCOM (`System.Management`): `StdRegProv` for the inventory, and `Win32_Process.Create` + `cmd /v:on` + a temporary registry value for the msiexec exit code. In install mode, packages are copied through the admin share `\\server\C$`. | WMI firewall rules; for install also SMB 445 and admin shares |

## Install mode

```text
MsiRemoteUninstall -c SRV01 --install "*.msi" -s \\fileserver\msi\test -u CONTOSO\admin
MsiRemoteUninstall -c SRV01 -i Contoso.HelloWorld-1.0.0 -s http://localhost:8080/msi/
MsiRemoteUninstall -c SRV01 -i D:\Packages\App.msi --source-mode direct      # path on the server
MsiRemoteUninstall -c SRV01 -i App.msi -p INSTALLFOLDER=D:\Apps\App -p FOO=bar
```

- **Where packages come from:** an `--install` entry can be a file name (looked up in
  `--package-source`, `.msi` optional, wildcards allowed), a full path, a UNC path or a URL.
  URLs are downloaded to `%TEMP%\MsiRemoteUninstall\` first.
- **`--source-mode copy`** (default): the package is copied to each server (`--remote-staging-folder`,
  default `C:\Windows\Temp\MsiRemoteInstall`), installed, then deleted unless `--keep-staged` is given.
  - `psremoting` sends the file in chunks through the WinRM session, so no extra port is needed.
  - `wmi` copies it through the `\\server\C$` admin share.
- **`--source-mode direct`:** msiexec on the server opens the path itself. Use this for a path on the
  server, or a UNC share the server's computer account can read. Remote sessions cannot pass your
  credentials on to a third machine (the "double hop" problem); see `--explain MSI1619`.
- **Already installed:** before installing, ProductName/ProductVersion/ProductCode are read with
  `msi.dll`. Packages whose ProductCode is already installed are skipped (`AlreadyInstalled`).
- **MSI properties:** `--msi-property` / `-p NAME=VALUE` passes public MSI properties on install and
  uninstall. The sample `FailingUninstall` package uses this: `-p FORCE_UNINSTALL=1`.

## Errors

The error catalog is shared with the other tools. The data in `shared/error-catalog.json` is
generated from the F# catalog and embedded into `MsiRemoteUninstall.Core.dll`.
`--errors`, `--explain <code>` and [`../ERROR-CODES.md`](../ERROR-CODES.md) list every code with
its explanation and fixes. Exit codes: 0 OK, 1 partial failure, 2 invalid input, 3 credentials,
4 aborted, 5 no server reachable, 6 internal error, 10 OK but reboot required.
