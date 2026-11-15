# Local MSI Testing Guide

You can test the build, install, discovery, and uninstall workflow on your own Windows computer. You do not need access to remote servers, WinRM, or other machines.

## 1. Build the MSI

From the toolkit folder:

```powershell
.\Scripts\Build-Msi.ps1 `
    -ProjectPath "C:\Source\MyApp\MyApp.fsproj" `
    -ProductName "HelloWorld" `
    -Version "1.0.0"
```

The MSI is created in the toolkit's `output` folder.

## 2. Install locally

Open PowerShell **as Administrator** and run:

```powershell
msiexec.exe /i ".\output\HelloWorld-1.0.0.msi" /qn /norestart
$LASTEXITCODE
```

Exit code `0` means success; `3010` means success with a reboot required.

For a verbose log:

```powershell
msiexec.exe /i ".\output\HelloWorld-1.0.0.msi" /qn /norestart /l*v ".\install.log"
```

## 3. Discover the installed MSI and ProductCode

```powershell
Get-ItemProperty `
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*", `
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*" |
Where-Object {
    $_.WindowsInstaller -eq 1 -and
    $_.DisplayName -like "*HelloWorld*"
} |
Select-Object DisplayName, DisplayVersion, PSChildName
```

`PSChildName` normally contains the MSI ProductCode GUID.

## 4. Uninstall locally

Use the ProductCode from the previous step:

```powershell
msiexec.exe /x "{YOUR-PRODUCT-CODE-GUID}" /qn /norestart
$LASTEXITCODE
```

## 5. Verify

Run the discovery command again. HelloWorld should no longer appear.

## 6. Optional: test the remote scripts against localhost

From an elevated PowerShell session, enable local remoting:

```powershell
Enable-PSRemoting -Force
```

Then use `-Servers "localhost"` with the inventory or uninstall scripts. **Caution:** uninstalling against `localhost` removes the matching product from your own computer. Use only the disposable test MSI.
