# PowerShell MSI Toolkit

A small toolkit for building an MSI from your own .NET F# console application and testing MSI discovery, installation, and uninstallation locally or on remote Windows servers.

**No MSI is included in this ZIP.** `Build-Msi.ps1` creates an MSI from the `.fsproj` you supply.

## Contents

```text
PowerShell-MSI-Toolkit/
├── README.md
├── Scripts/
│   ├── Build-Msi.ps1
│   ├── Get-RemoteMsiInventory.ps1
│   ├── Install-RemoteMsi.ps1
│   ├── Uninstall-RemoteMsi.ps1
│   └── Install-LocalMsi.ps1
└── Docs/
    └── Local-MSI-Testing-Guide.md
```

## Requirements

### Build an MSI

- Windows 10/11 or Windows Server.
- .NET SDK installed and available as `dotnet`.
- Internet access on the first build to restore the WiX Toolset SDK from NuGet.
- An existing F# `.fsproj` console application.

The builder uses the WiX Toolset MSBuild SDK (7.0.1). The generated MSI is x64 and installs per-machine under `Program Files`. Self-contained publishing is enabled by default, so the target machine does not need the matching .NET runtime.

### Remote operations

- PowerShell Remoting / WinRM enabled and reachable on each target server.
- Credentials with local administrator rights on the target servers.
- Firewall and network rules permitting WinRM.
- The scripts use Windows PowerShell 5.1-compatible syntax.

## 1. Build an MSI from your F# application

From the toolkit folder:

```powershell
.\Scripts\Build-Msi.ps1 `
    -ProjectPath "C:\Source\MyApp\MyApp.fsproj" `
    -ProductName "HelloWorld" `
    -Manufacturer "MyCompany" `
    -Version "1.0.0"
```

The MSI is written to the toolkit's `output` folder, for example:

```text
output\HelloWorld-1.0.0.msi
```

Optional parameters:

```powershell
# Use the machine's installed .NET runtime instead of bundling it:
-SelfContained $false

# Choose another output folder:
-OutputDirectory "C:\Build\Packages"
```

The builder publishes for `win-x64`. The `UpgradeCode` is deterministic for the same manufacturer/product name; keep those values consistent between versions of the same product.

## 2. Discover installed MSI packages

Find all registered MSI products on multiple servers:

```powershell
.\Scripts\Get-RemoteMsiInventory.ps1 `
    -Servers "SERVER01","SERVER02"
```

Filter by a case-insensitive substring of the MSI display name:

```powershell
.\Scripts\Get-RemoteMsiInventory.ps1 `
    -Servers "SERVER01","SERVER02" `
    -NameFilter "Hello"
```

Use an empty filter to list all registered MSI products:

```powershell
.\Scripts\Get-RemoteMsiInventory.ps1 `
    -Servers "SERVER01" `
    -NameFilter ""
```

The output includes server, display name, version, and ProductCode GUID. The script reads the 32-bit and 64-bit uninstall registry locations and does not use `Win32_Product`.

## 3. Install an MSI remotely

The install script copies the MSI to a temporary folder on each server, runs a silent install, captures the exit code, and attempts to remove the temporary folder:

```powershell
.\Scripts\Install-RemoteMsi.ps1 `
    -MsiPath ".\output\HelloWorld-1.0.0.msi" `
    -Servers "SERVER01","SERVER02"
```

Credentials are requested once if `-Credential` is not supplied. The account needs administrative rights on the target servers.

Exit codes:
- `0`: installed successfully.
- `3010`: installed successfully; reboot required.
- Other codes: installation failed. The remote verbose log path is included in the result.

## 4. Uninstall MSI packages remotely

Supply ProductCode GUIDs (not package names):

```powershell
.\Scripts\Uninstall-RemoteMsi.ps1 `
    -Servers "SERVER01","SERVER02" `
    -MsiGuids "{11111111-2222-3333-4444-555555555555}","{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"
```

The script reports a result for each server/GUID pair.

Common exit codes:
- `0`: uninstalled successfully.
- `1605`: product is not installed.
- `1614`: product is already uninstalled.
- `3010`: uninstalled successfully; reboot required.
- Other codes: failure; inspect the log path reported by the script.

## 5. Test locally without remote servers

Build your MSI, then open an elevated PowerShell window and install it locally:

```powershell
msiexec.exe /i ".\output\HelloWorld-1.0.0.msi" /qn /norestart
$LASTEXITCODE
```

Discover the MSI ProductCode locally:

```powershell
Get-ItemProperty `
  "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*", `
  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*" |
Where-Object {
    $_.WindowsInstaller -eq 1 -and $_.DisplayName -like "*HelloWorld*"
} |
Select-Object DisplayName, DisplayVersion, PSChildName
```

Uninstall locally using the discovered ProductCode:

```powershell
msiexec.exe /x "{YOUR-PRODUCT-CODE-GUID}" /qn /norestart
$LASTEXITCODE
```

See `Docs\Local-MSI-Testing-Guide.md` for the full local test walkthrough.

### Optional: test remote scripts against localhost

You can set `-Servers "localhost"` and enable local PowerShell Remoting from an elevated PowerShell session:

```powershell
Enable-PSRemoting -Force
```

Be aware that an uninstall script targeting `localhost` will uninstall the matching MSI from your own computer. Use only the disposable test package.

## Safety and operational notes

- Test with a disposable application and a test machine before production use.
- The install/uninstall scripts use `/qn /norestart`; there is no interactive MSI UI and they do not automatically reboot the server.
- Remote installation transfers the MSI from your computer to each server over the PowerShell remoting session. It does not require a shared network drive.
- Remote MSI logs are created under the remote Windows temporary directory. The install script removes its temporary deployment directory after a successful install; if installation fails, it keeps the remote folder and log so you can troubleshoot. Uninstall logs are left in the remote machine's temporary directory.
- Uninstall logs are left in the remote machine's temporary directory.
- `Win32_Product` is intentionally avoided because querying it can trigger Windows Installer consistency checks or repairs.
- This is a practical starting point, not a substitute for change control, code signing, and testing in your environment.

## Troubleshooting

### WinRM / access denied

Confirm that PowerShell Remoting is enabled and reachable on the target server, and that the supplied account is an administrator there.

### MSI returns 1618

Another Windows Installer operation is already running. Wait for it to finish and retry.

### MSI returns 1603

This is a generic fatal installation error. Inspect the verbose MSI log for the underlying cause.

### WiX restore/build errors

Check that the .NET SDK is installed and that the machine can reach NuGet on the first build. The builder uses WiX Toolset SDK 7.0.1 through MSBuild.
