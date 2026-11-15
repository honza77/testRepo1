[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath
)

$ErrorActionPreference = "Stop"
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
if ([IO.Path]::GetExtension($MsiPath) -ne ".msi") {
    throw "MsiPath must point to an .msi file."
}

Write-Host "Installing $MsiPath locally..." -ForegroundColor Cyan
$logPath = Join-Path $PSScriptRoot "local-install.log"
$arguments = "/i `"$MsiPath`" /qn /norestart /l*v `"$logPath`""
$process = Start-Process -FilePath "msiexec.exe" -ArgumentList $arguments -Wait -PassThru

Write-Host "MSI exit code: $($process.ExitCode)"
Write-Host "Install log: $logPath"

if ($process.ExitCode -notin @(0, 3010)) {
    throw "MSI installation failed with exit code $($process.ExitCode)."
}

Write-Host "Installed. Discover the ProductCode with Get-RemoteMsiInventory.ps1 using localhost only if local WinRM is enabled, or query the local registry." -ForegroundColor Green
