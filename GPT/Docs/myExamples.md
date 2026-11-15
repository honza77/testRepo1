
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

```powershell
.\Scripts\Build-Msi.ps1 `
    -ProjectPath "C:\git\testRepo1\dotnet\ConsoleApp1\ConsoleApp1\ConsoleApp1.fsproj" `
    -ProductName "HelloWorld" `
    -Version "1.0.0"
```

```
.\Scripts\Build-Msi-Wix5.ps1 `
    -ProjectPath "C:\git\testRepo1\dotnet\ConsoleApp1\ConsoleApp1\ConsoleApp1.fsproj" `
    -ProductName "HelloWorld" `
    -Manufacturer "MyCompany" `
    -Version "1.0.0"
```    

>msiexec.exe /i "..\dotnet\ConsoleApp1\ConsoleApp1\artifacts\HelloWorld-1.0.0.msi" /qn /norestart

// with log
>msiexec.exe /i "..\dotnet\ConsoleApp1\ConsoleApp1\artifacts\HelloWorld-1.0.0.msi" /qn /norestart /l*v ".\install.log"


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



```powershell - Admin
$msi = "C:\git\testRepo1\dotnet\ConsoleApp1\ConsoleApp1\artifacts\HelloWorld-1.0.0.msi"

$p = Start-Process msiexec.exe `
    -ArgumentList "/i `"$msi`" /norestart /l*v `"$PWD\install.log`"" `
    -Verb RunAs `
    -Wait `
    -PassThru

"MSI exit code: $($p.ExitCode)"
```


## check

* with GUID
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

* somwwhat simplier    
    ```powershell
    Get-ItemProperty `
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*" |
    Where-Object {
        $_.DisplayName -like "*HelloWorld*"
    } |
    Select-Object DisplayName, DisplayVersion, InstallLocation
    ```

* lists dlls
    ```powershell
    Get-ChildItem "C:\Program Files\HelloWorld"
    ```
