# Sample apps and MSI packages for testing

These samples give you a set of MSIs to install on test servers, so you can try out uninstalling,
listing and every result code with the tools in this repository.

| Folder / file | Content |
|---|---|
| `HelloWorld.CSharp/` | C# console app (.NET Framework 4.8.1) |
| `HelloWorld.FSharp/` | F# console app (.NET Framework 4.8.1, 32-bit) |
| `Installer/` | WiX v5 project (`WixToolset.Sdk`). One `Package.wxs`, built in several variants. |
| `Build-SampleMsis.ps1` | Builds the apps and all MSI variants into `out\msi` |
| `Start-PackageServer.ps1` | Serves `out\msi` over HTTP for testing URL package sources |

## Build the MSIs (Windows)

You need the .NET 8 SDK. WiX is restored from NuGet automatically.

```powershell
cd samples
.\Build-SampleMsis.ps1
```

| MSI | Product | What its uninstall does |
|---|---|---|
| `Contoso.HelloWorld-1.0.0.msi` | Contoso HelloWorld 1.0.0 (x64) | removes cleanly, exit 0 |
| `Contoso.HelloWorld-2.0.0.msi` | Contoso HelloWorld 2.0.0 (x64) | removes cleanly; installing it **upgrades** 1.0.0 |
| `Fabrikam.HelloWorld.x86.msi` | Fabrikam HelloWorld (x86) | 32-bit product (registry `WOW6432Node`), F# app |
| `Contoso.RebootRequired.msi` | Contoso HelloWorld RebootRequired | exit 3010, so you see `MSI3010` and tool exit code 10 |
| `Contoso.FailingUninstall.msi` | Contoso HelloWorld FailingUninstall | exit 1603, so you see `MSI1603` and tool exit code 1. Removable with `-p FORCE_UNINSTALL=1` |
| `Contoso.SlowUninstall.msi` | Contoso HelloWorld SlowUninstall | takes about 150 s; with `--timeout 1` you see `MRU403` |

The script also writes `out\msi\packages.txt` (for `--install-list`) and `out\msi\uninstall.txt`
(for `--msi-list` / `-MsiListPath`). Each product writes a marker to
`HKLM\Software\MsiRemoteUninstallSamples\<ProductName>` and installs its app to
`C:\Program Files\<Manufacturer>\<ProductName>`.

## Test walkthrough

Below, `MRU` stands for either tool, run from its build output folder:
`dotnet\MsiRemoteUninstall\bin\Release\net8.0\MsiRemoteUninstall.exe` (F#) or
`netfx\src\MsiRemoteUninstall\bin\Release\net481\MsiRemoteUninstall.exe` (C#).

```powershell
$msi = '.\samples\out\msi'

# 1. Install all test packages (copied to the server over WinRM)
MRU -c SRV01 --install-list $msi\packages.txt -s $msi -u CONTOSO\admin -y

# 2. Look at them
MRU -c SRV01 --list -f HelloWorld
.\Uninstall-RemoteMsi.v2.ps1 -ComputerName SRV01 -ListInstalled -FilterMode Wildcard -Filter '*HelloWorld*'

# 3. Major upgrade 1.0.0 -> 2.0.0
MRU -c SRV01 -i Contoso.HelloWorld-2.0.0 -s $msi -y

# 4. Dry run, then uninstall (expect: Success, SuccessRebootRequired / MSI3010, exit code 10)
MRU -c SRV01 --msi-list $msi\uninstall.txt --what-if
MRU -c SRV01 --msi-list $msi\uninstall.txt -y

# 5. Failure handling: MSI1603 with remediation, then the forced removal
MRU -c SRV01 -m "Contoso HelloWorld FailingUninstall" -y
MRU -c SRV01 -m "Contoso HelloWorld FailingUninstall" -p FORCE_UNINSTALL=1 -y

# 6. Timeout handling (MRU403) - reinstall the slow package first
MRU -c SRV01 -i Contoso.SlowUninstall -s $msi -y
MRU -c SRV01 -m "Contoso HelloWorld SlowUninstall" --timeout 1 -y

# 7. Same with the WMI transport (copies through \\SRV01\C$)
MRU -c SRV01 -t wmi --install-list $msi\packages.txt -s $msi -y
MRU -c SRV01 -t wmi --msi-list $msi\uninstall.txt -y

# 8. Install from a URL package source
.\samples\Start-PackageServer.ps1 -LocalOnly        # in a second window
MRU -c SRV01 -i Contoso.HelloWorld-1.0.0 -s http://localhost:8080/msi/ -y

# 9. Install from a share that the server reads itself (needs read access for SRV01$)
MRU -c SRV01 -i "*.msi" -s \\fileserver\msi --source-mode direct -y
```

The PowerShell scripts (`Uninstall-RemoteMsi.ps1`, `Uninstall-RemoteMsi.v2.ps1`) only uninstall and
list. To prepare servers for them, install with one of the .NET tools.
