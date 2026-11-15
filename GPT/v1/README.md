# F# .NET application → MSI builder

This package contains `Build-Msi.ps1`. It publishes an existing F# `.fsproj` and wraps the published files in a Windows Installer (`.msi`) using the WiX Toolset MSBuild SDK.

It does **not** generate a HelloWorld application. It packages your own F# console application.

## Requirements

- Windows PowerShell 5.1 or PowerShell 7.
- .NET SDK 8.0 or later (the project uses WiX Toolset SDK 7.0.1).
- Internet access on the first build so NuGet can restore the WiX SDK.
- An existing F# console application project (`.fsproj`).

WiX is restored as an MSBuild SDK; you do not need to install the separate `wix.exe` global tool.

## Quick start

Open PowerShell in the extracted folder and run:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

.\Build-Msi.ps1 `
    -ProjectPath "C:\src\MyHelloWorld\MyHelloWorld.fsproj"
```

By default, the script:

1. Publishes the F# project in Release configuration for `win-x64`.
2. Uses a self-contained publish, so the target server does not need the .NET runtime installed.
3. Creates an x64, per-machine MSI.
4. Installs the application files under `C:\Program Files\<ProjectName>`.
5. Writes the MSI to the `output` subfolder.

## Examples

### Choose product name, manufacturer and version

```powershell
.\Build-Msi.ps1 `
    -ProjectPath "C:\src\MyApp\MyApp.fsproj" `
    -ProductName "HelloWorld" `
    -Manufacturer "Contoso" `
    -Version "1.0.0"
```

### Framework-dependent deployment

If the target servers already have the required .NET runtime:

```powershell
.\Build-Msi.ps1 `
    -ProjectPath "C:\src\MyApp\MyApp.fsproj" `
    -ProductName "HelloWorld" `
    -SelfContained $false
```

### Choose another output directory

```powershell
.\Build-Msi.ps1 `
    -ProjectPath "C:\src\MyApp\MyApp.fsproj" `
    -OutputDirectory "C:\Build\Packages"
```

### ARM64 publish

The sample MSI authoring in this script is configured for x64. Do not use `win-arm64` without also changing the MSI package architecture and install folder in the script.

## Install and uninstall the MSI

Install locally from an elevated PowerShell window:

```powershell
Start-Process msiexec.exe -Wait -PassThru -ArgumentList `
    '/i "C:\Build\output\HelloWorld-1.0.0.msi" /qn /norestart'
```

Uninstall using the ProductCode GUID shown in the MSI's uninstall registry entry:

```powershell
Start-Process msiexec.exe -Wait -PassThru -ArgumentList `
    '/x "{PRODUCT-CODE-GUID}" /qn /norestart'
```

For a test run, you can also install with a verbose log:

```powershell
msiexec.exe /i ".\output\HelloWorld-1.0.0.msi" /qn /norestart /l*v ".\install.log"
```

## Important notes

- The MSI installs files; it does not create a Start Menu shortcut or Windows service. Run the installed console application's `.exe` from its installation folder.
- The script creates a stable `UpgradeCode` for this sample product. Keep that value unchanged when building later versions of the same product. Change it only when creating a distinct product.
- WiX generates the MSI ProductCode. Use the ProductCode from the installed product's uninstall registry entry when testing your remote uninstall script.
- The MSI is configured for x64 and per-machine installation. Run installation with administrator rights.
- `SelfContained` defaults to `true`; this makes the MSI larger but avoids requiring the matching .NET runtime on the destination server.
- Test the generated MSI on a disposable test machine before deploying it to production servers.
- This is a packaging starting point. For production software, consider signing the MSI, adding a license/UI if needed, and reviewing upgrade, repair, and major-upgrade behavior.

## Troubleshooting

### `dotnet` is not recognized

Install the .NET SDK and reopen PowerShell.

### NuGet restore fails

Check network access to NuGet and try:

```powershell
dotnet restore
```

### The application targets a different runtime

Pass the appropriate runtime identifier, for example `win-x64`. The MSI template currently targets x64 Windows, so the runtime identifier should also be `win-x64`.

### Build fails in WiX

The script uses WiX Toolset SDK 7.0.1. Check the build output for the first WiX/MSBuild error; it is usually more useful than the final build-failed message.
