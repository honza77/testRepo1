# Local MSI Testing Guide

You can test the complete MSI build, install, discovery, and uninstall workflow on your own Windows computer. You do not need access to remote servers, WinRM, or other machines.

## 1. Build the MSI

Extract the `FSharp-MSI-Builder.zip` bundle. Open PowerShell in the extracted folder and run the build script against your F# project:

```powershell
.\Build-Msi.ps1 `
    -ProjectPath "C:\Source\MyApp\MyApp.fsproj" `
    -ProductName "HelloWorld" `
    -Version "1.0.0"
```

The MSI should be created in the bundle's `output` folder, for example:

```text
output\
    HelloWorld-1.0.0.msi
```

## 2. Install the MSI locally

Open PowerShell **as Administrator**, change to the bundle folder, and run:

```powershell
msiexec.exe /i ".\output\HelloWorld-1.0.0.msi" /qn /norestart
```

Check the exit code immediately afterwards:

```powershell
$LASTEXITCODE
```

An exit code of `0` means the installation succeeded. `3010` means it succeeded but a reboot is required.

For troubleshooting, you can enable a verbose installation log:

```powershell
msiexec.exe /i ".\output\HelloWorld-1.0.0.msi" /qn /norestart /l*v ".\install.log"
```

## 3. Discover the installed MSI

Run this locally to find the installed HelloWorld MSI and its ProductCode GUID:

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

## 4. Uninstall the MSI locally

Use the ProductCode GUID returned by the previous step:

```powershell
msiexec.exe /x "{YOUR-PRODUCT-GUID}" /qn /norestart
```

Check the exit code immediately afterwards:

```powershell
$LASTEXITCODE
```

An exit code of `0` means the uninstall succeeded. `3010` means it succeeded but a reboot is required.

## 5. Verify the uninstall

Run the discovery command from step 3 again. HelloWorld should no longer appear.

## 6. Optional: test the remote scripts against localhost

You can also test the remote PowerShell scripts on your own computer by setting:

```powershell
$Servers = @("localhost")
```

PowerShell Remoting must be enabled locally. From an elevated PowerShell session, enable it with:

```powershell
Enable-PSRemoting -Force
```

Then run your discovery or uninstall script. Use the same credentials and script logic you intend to use remotely.

> **Caution:** The uninstall script will uninstall the matching product from your local computer when `localhost` is the target. Test only with your disposable HelloWorld MSI.

## Suggested end-to-end test

1. Build `HelloWorld.msi` from your F# project.
2. Install it locally.
3. Run the MSI discovery script and confirm that HelloWorld and its ProductCode are listed.
4. Uninstall it using the ProductCode.
5. Run discovery again and confirm that HelloWorld is no longer listed.

This validates the MSI and the basic install/discovery/uninstall workflow before you use the scripts against remote servers.
