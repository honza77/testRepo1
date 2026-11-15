
# Remote servers
$Servers = @(
    "SERVER01",
    "SERVER02",
    "SERVER03"
)

# MSI product names (as registered in Windows)
$MsiPackages = @(
    "Package Name 1",
    "Package Name 2"
)

# Ask for credentials
$Credential = Get-Credential

foreach ($Server in $Servers) {
    Write-Host "`nConnecting to $Server..." -ForegroundColor Cyan

    try {
        Invoke-Command -ComputerName $Server -Credential $Credential -ScriptBlock {
            param($Packages)

            foreach ($Package in $Packages) {
                $App = Get-WmiObject -Class Win32_Product |
                    Where-Object { $_.Name -eq $Package }

                if ($App) {
                    Write-Host "Uninstalling $($App.Name)..."
                    $Result = $App.Uninstall()

                    Write-Host "Result: $($Result.ReturnValue)"
                }
                else {
                    Write-Host "Package not found: $Package"
                }
            }
        } -ArgumentList (,$MsiPackages) -ErrorAction Stop
    }
    catch {
        Write-Host "ERROR on $Server : $_" -ForegroundColor Red
    }
}
