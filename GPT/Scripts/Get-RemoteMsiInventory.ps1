[CmdletBinding()]
param(
    [string[]]$Servers = @("SERVER01"),
    [string]$NameFilter = "",
    [PSCredential]$Credential
)

$ErrorActionPreference = "Stop"
if (-not $Credential) { $Credential = Get-Credential }

$results = foreach ($server in $Servers) {
    Write-Host "Checking $server..." -ForegroundColor Cyan
    try {
        Invoke-Command -ComputerName $server -Credential $Credential -ErrorAction Stop `
            -ScriptBlock {
                param([string]$Filter)

                $paths = @(
                    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
                    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
                )

                Get-ItemProperty -Path $paths -ErrorAction SilentlyContinue |
                    Where-Object {
                        $_.WindowsInstaller -eq 1 -and
                        $_.DisplayName -and
                        ([string]::IsNullOrWhiteSpace($Filter) -or
                         $_.DisplayName.IndexOf($Filter, [StringComparison]::OrdinalIgnoreCase) -ge 0)
                    } |
                    Select-Object @{
                        Name = "Server"; Expression = { $env:COMPUTERNAME }
                    }, @{
                        Name = "DisplayName"; Expression = { $_.DisplayName }
                    }, @{
                        Name = "Version"; Expression = { $_.DisplayVersion }
                    }, @{
                        Name = "ProductCode"; Expression = { $_.PSChildName }
                    }
            } -ArgumentList $NameFilter
    }
    catch {
        [PSCustomObject]@{
            Server = $server
            DisplayName = $null
            Version = $null
            ProductCode = $null
            Error = $_.Exception.Message
        }
    }
}

if ($results) {
    $results | Format-Table -AutoSize
}
else {
    Write-Host "No matching MSI packages found."
}
