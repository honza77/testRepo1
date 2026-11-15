
# usage:

#$Servers = @(
#    "SERVER01",
#    "SERVER02",
#    "SERVER03"
#)
#
#$MsiGuids = @(
#    "{956ECF1E-28A1-44A8-8D14-53E62E218742}"
#)
#
#.\Scripts\Uninstall-RemoteMsi.ps1 `
#    -Servers $Servers `
#    -MsiGuids $MsiGuids

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Servers,

    [Parameter(Mandatory = $true)]
    [string[]]$MsiGuids,

    [System.Management.Automation.PSCredential]$Credential,

    [int]$ThrottleLimit = 32
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

# ------------------------------------------------------------
# Get credentials from the console if not supplied as parameter
# ------------------------------------------------------------

if (-not $Credential) {
    Write-Host "Enter credentials for remote servers" -ForegroundColor Cyan

    $Username = Read-Host "Username"
    $Password = Read-Host "Password" -AsSecureString

    $Credential = New-Object System.Management.Automation.PSCredential(
        $Username,
        $Password
    )
}

# ------------------------------------------------------------
# Validate MSI Product Codes
# ------------------------------------------------------------

$ValidatedGuids = @()

foreach ($Guid in $MsiGuids) {
    $NormalizedGuid = $Guid.Trim().Trim('{', '}')

    if ($NormalizedGuid -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
        throw "Invalid MSI ProductCode: $Guid"
    }

    $ValidatedGuids += "{$NormalizedGuid}"
}

# ------------------------------------------------------------
# Execute uninstall remotely on all servers
# ------------------------------------------------------------

Write-Host ""
Write-Host "Starting MSI uninstall on $($Servers.Count) server(s)..." -ForegroundColor Cyan
Write-Host ""

$Results = Invoke-Command `
    -ComputerName $Servers `
    -Credential $Credential `
    -ThrottleLimit $ThrottleLimit `
    -ArgumentList (,$ValidatedGuids) `
    -ScriptBlock {

        param(
            [string[]]$ProductCodes
        )

        $LogDirectory = "C:\Windows\Temp\MsiUninstallLogs"

        New-Item -ItemType Directory -Path $LogDirectory -Force |
            Out-Null

        foreach ($ProductCode in $ProductCodes) {

            $LogPath = Join-Path $LogDirectory (
                "Uninstall-{0}-{1}.log" -f
                $ProductCode.Trim('{}'),
                (Get-Date -Format "yyyyMMdd-HHmmss")
            )

            try {
                $Arguments = "/x $ProductCode /qn /norestart /l*v `"$LogPath`""

                $Process = Start-Process `
                    -FilePath "$env:WINDIR\System32\msiexec.exe" `
                    -ArgumentList $Arguments `
                    -Wait `
                    -PassThru

                $ExitCode = $Process.ExitCode

                switch ($ExitCode) {
                    0 {
                        $Status = "Success"
                    }
                    1605 {
                        $Status = "NotInstalled"
                    }
                    1614 {
                        $Status = "AlreadyUninstalled"
                    }
                    3010 {
                        $Status = "Success - RebootRequired"
                    }
                    default {
                        $Status = "Failed"
                    }
                }

                [PSCustomObject]@{
                    Server      = $env:COMPUTERNAME
                    ProductCode = $ProductCode
                    ExitCode    = $ExitCode
                    Status      = $Status
                    LogPath     = $LogPath
                }
            }
            catch {
                [PSCustomObject]@{
                    Server      = $env:COMPUTERNAME
                    ProductCode = $ProductCode
                    ExitCode    = -1
                    Status      = "Error: $($_.Exception.Message)"
                    LogPath     = $LogPath
                }
            }
        }
    } -ErrorAction Continue

# ------------------------------------------------------------
# Display results
# ------------------------------------------------------------

Write-Host ""
Write-Host "MSI uninstall results" -ForegroundColor Cyan
Write-Host ""

$Results |
    Select-Object Server, ProductCode, ExitCode, Status |
    Format-Table -AutoSize

Write-Host ""
Write-Host "Remote MSI logs are stored in:"
Write-Host "C:\Windows\Temp\MsiUninstallLogs"
