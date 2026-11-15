[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,

    [string[]]$Servers = @("SERVER01"),

    [PSCredential]$Credential
)

$ErrorActionPreference = "Stop"
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
if ([IO.Path]::GetExtension($MsiPath) -ne ".msi") {
    throw "MsiPath must point to an .msi file."
}
if (-not $Credential) { $Credential = Get-Credential }

$results = foreach ($server in $Servers) {
    $keepRemoteLog = $false
    $session = $null
    $remoteFolder = "C:\Windows\Temp\MsiDeploy_$([guid]::NewGuid().ToString('N'))"
    $remoteMsi = Join-Path $remoteFolder ([IO.Path]::GetFileName($MsiPath))
    $remoteLog = Join-Path $remoteFolder "install.log"

    Write-Host "Installing on $server..." -ForegroundColor Cyan
    try {
        $session = New-PSSession -ComputerName $server -Credential $Credential -ErrorAction Stop

        Invoke-Command -Session $session -ScriptBlock {
            param($Folder)
            New-Item -ItemType Directory -Path $Folder -Force | Out-Null
        } -ArgumentList $remoteFolder -ErrorAction Stop

        Copy-Item -LiteralPath $MsiPath -Destination $remoteMsi -ToSession $session -ErrorAction Stop

        $result = Invoke-Command -Session $session -ScriptBlock {
            param($PackagePath, $LogPath, $PackageName)

            $arguments = "/i `"$PackagePath`" /qn /norestart /l*v `"$LogPath`""
            $process = Start-Process -FilePath "msiexec.exe" -ArgumentList $arguments `
                -Wait -PassThru

            $status = switch ($process.ExitCode) {
                0    { "Installed" }
                3010 { "Installed - reboot required" }
                default { "Failed" }
            }

            [PSCustomObject]@{
                Server = $env:COMPUTERNAME
                Package = $PackageName
                ExitCode = $process.ExitCode
                Status = $status
                LogPath = $LogPath
            }
        } -ArgumentList $remoteMsi, $remoteLog, ([IO.Path]::GetFileName($MsiPath)) -ErrorAction Stop

        if ($result.ExitCode -notin @(0, 3010)) { $keepRemoteLog = $true }
        $result
    }
    catch {
        [PSCustomObject]@{
            Server = $server
            Package = [IO.Path]::GetFileName($MsiPath)
            ExitCode = $null
            Status = "Connection/deployment error"
            Error = $_.Exception.Message
        }
    }
    finally {
        if ($session -and -not $keepRemoteLog) {
            try {
                Invoke-Command -Session $session -ScriptBlock {
                    param($Folder)
                    Remove-Item -LiteralPath $Folder -Recurse -Force -ErrorAction SilentlyContinue
                } -ArgumentList $remoteFolder -ErrorAction SilentlyContinue
            } catch {}
            Remove-PSSession -Session $session -ErrorAction SilentlyContinue
        }
    }
}

$results | Format-Table -AutoSize
