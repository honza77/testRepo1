[CmdletBinding()]
param(
    [string[]]$Servers = @("SERVER01"),

    [Parameter(Mandatory = $true)]
    [string[]]$MsiGuids,

    [PSCredential]$Credential
)



$ErrorActionPreference = "Stop"
if (-not $Credential) {
        # $Credential = Get-Credential 

        $Username = Read-Host "Enter username"
        $Password = Read-Host "Enter password" -AsSecureString

        $Credential = New-Object System.Management.Automation.PSCredential(
            $Username,
            $Password
        )     
    }

foreach ($guid in $MsiGuids) {
    if ($guid -notmatch '^\{?[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\}?$') {
        throw "Invalid MSI ProductCode GUID: $guid"
    }
}

$results = foreach ($server in $Servers) {
    Write-Host "Uninstalling on $server..." -ForegroundColor Cyan
    try {
        Invoke-Command -ComputerName $server -Credential $Credential -ErrorAction Stop `
            -ScriptBlock {
                param([string[]]$Guids)

                foreach ($guid in $Guids) {
                    $normalizedGuid = $guid.Trim("{}")
                    $logPath = Join-Path $env:TEMP ("msi-uninstall-" + $normalizedGuid + ".log")
                    $arguments = "/x `"$guid`" /qn /norestart /l*v `"$logPath`""
                    $process = Start-Process -FilePath "msiexec.exe" -ArgumentList $arguments `
                        -Wait -PassThru

                    $status = switch ($process.ExitCode) {
                        0    { "Uninstalled" }
                        1605 { "Not installed" }
                        1614 { "Already uninstalled" }
                        3010 { "Uninstalled - reboot required" }
                        default { "Failed" }
                    }

                    [PSCustomObject]@{
                        Server = $env:COMPUTERNAME
                        ProductCode = $guid
                        ExitCode = $process.ExitCode
                        Status = $status
                        LogPath = $logPath
                    }
                }
            } -ArgumentList (,$MsiGuids)
    }
    catch {
        [PSCustomObject]@{
            Server = $server
            ProductCode = $null
            ExitCode = $null
            Status = "Connection/remote execution error"
            Error = $_.Exception.Message
        }
    }
}

$results | Format-Table -AutoSize
