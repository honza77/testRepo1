
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