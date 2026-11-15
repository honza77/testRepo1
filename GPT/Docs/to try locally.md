Yes. Just change:
$Servers = @("localhost")


Or even:
$Servers = @("localhost", "SERVER01", "SERVER02")


A couple of things to note:
- PowerShell Remoting must be enabled locally (Enable-PSRemoting -Force, run as Administrator).
- Run PowerShell as Administrator.
- Use the actual MSI ProductCodes (GUIDs), not UpgradeCodes.
- You can verify installation first with:
Get-CimInstance Win32_Product |
    Select-Object Name, IdentifyingNumber


Note: Win32_Product can trigger MSI consistency checks, so for routine inventory I'd avoid it.