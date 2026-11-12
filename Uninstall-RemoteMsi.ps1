<#
.SYNOPSIS
    Uninstalls one or more MSI products from one or more remote Windows servers.

.DESCRIPTION
    Connects to each target server over PowerShell Remoting (WinRM) using the
    supplied (or prompted) credentials, finds installed MSI products matching the
    given names / product codes, and uninstalls them silently with msiexec.

    Servers and MSIs can be passed directly on the command line, read from text
    files, or both (the lists are merged and de-duplicated).

    MSI identifiers can be either:
      * A product code GUID, e.g. {12345678-90AB-CDEF-1234-567890ABCDEF}
      * A display name as shown in "Programs and Features". Wildcards are
        supported, e.g. "Contoso Agent*".

    Installed products are discovered from the registry Uninstall keys (both
    64-bit and 32-bit views). Win32_Product is intentionally NOT used because
    querying it triggers a consistency check / repair of every MSI on the box.

    Results are written to the pipeline and to a CSV report.

.PARAMETER ComputerName
    One or more server host names.

.PARAMETER ComputerListPath
    Path to a text file with one server host name per line.

.PARAMETER MsiName
    One or more MSI display names (wildcards allowed) or product code GUIDs.

.PARAMETER MsiListPath
    Path to a text file with one MSI display name or product code per line.

.PARAMETER Credential
    Credentials used to connect to the servers. If omitted you will be prompted.

.PARAMETER UseSSL
    Connect to WinRM over HTTPS (port 5986).

.PARAMETER ThrottleLimit
    Maximum number of servers processed in parallel. Default 16.

.PARAMETER ReportPath
    Path of the CSV report. Default: .\MsiUninstallReport_<timestamp>.csv

.PARAMETER RemoteLogFolder
    Folder on each remote server where verbose msiexec logs are written.
    Default: C:\Windows\Temp\MsiUninstallLogs

.EXAMPLE
    .\Uninstall-RemoteMsi.ps1 -ComputerListPath .\servers.txt -MsiListPath .\msis.txt

    Reads servers and MSIs from files, prompts for credentials, asks for
    confirmation and uninstalls.

.EXAMPLE
    .\Uninstall-RemoteMsi.ps1 -ComputerName SRV01,SRV02 -MsiName 'Contoso Agent*' -WhatIf

    Dry run: connects and reports which products WOULD be removed, removes nothing.

.EXAMPLE
    $cred = Get-Credential CONTOSO\admin
    .\Uninstall-RemoteMsi.ps1 -ComputerListPath .\servers.txt -MsiName '{12345678-90AB-CDEF-1234-567890ABCDEF}' -Credential $cred -Confirm:$false

    Non-interactive run (no confirmation prompt) with pre-built credentials.

.NOTES
    Requirements:
      * PowerShell Remoting (WinRM) enabled on the target servers.
      * The account must be a local administrator on the target servers.
    Input files: blank lines and lines starting with '#' are ignored.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string[]] $ComputerName,

    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $ComputerListPath,

    [string[]] $MsiName,

    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $MsiListPath,

    [System.Management.Automation.PSCredential]
    [System.Management.Automation.Credential()]
    $Credential = [System.Management.Automation.PSCredential]::Empty,

    [switch] $UseSSL,

    [ValidateRange(1, 256)]
    [int] $ThrottleLimit = 16,

    [string] $ReportPath = (Join-Path -Path (Get-Location) -ChildPath ("MsiUninstallReport_{0:yyyyMMdd_HHmmss}.csv" -f (Get-Date))),

    [string] $RemoteLogFolder = 'C:\Windows\Temp\MsiUninstallLogs'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#region Helpers
function Read-ListFile {
    param([Parameter(Mandatory)][string] $Path)
    Get-Content -LiteralPath $Path |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
}

function Merge-List {
    param([string[]] $Inline, [string] $FilePath)
    $items = @()
    if ($Inline)   { $items += $Inline | ForEach-Object { $_.Trim() } | Where-Object { $_ } }
    if ($FilePath) { $items += Read-ListFile -Path $FilePath }
    # Case-insensitive de-duplication, preserving order
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($i in $items) { if ($seen.Add($i)) { $i } }
}
#endregion

#region Build input lists
$servers = @(Merge-List -Inline $ComputerName -FilePath $ComputerListPath)
$msis    = @(Merge-List -Inline $MsiName      -FilePath $MsiListPath)

if ($servers.Count -eq 0) { throw 'No servers specified. Use -ComputerName and/or -ComputerListPath.' }
if ($msis.Count -eq 0)    { throw 'No MSIs specified. Use -MsiName and/or -MsiListPath.' }

if ($Credential -eq [System.Management.Automation.PSCredential]::Empty) {
    $Credential = Get-Credential -Message 'Enter credentials with local admin rights on the target servers'
    if (-not $Credential) { throw 'No credentials supplied.' }
}

Write-Host ''
Write-Host ("Servers ({0}): {1}" -f $servers.Count, ($servers -join ', ')) -ForegroundColor Cyan
Write-Host ("MSIs    ({0}): {1}" -f $msis.Count,    ($msis    -join ', ')) -ForegroundColor Cyan
Write-Host ''

$dryRun = [bool]$WhatIfPreference
if (-not $dryRun) {
    $target = "{0} server(s)" -f $servers.Count
    $action = "Uninstall MSI(s): {0}" -f ($msis -join ', ')
    if (-not $PSCmdlet.ShouldProcess($target, $action)) {
        Write-Warning 'Aborted by user.'
        return
    }
}
else {
    Write-Host 'WhatIf mode: products will be discovered but NOT uninstalled.' -ForegroundColor Yellow
}
#endregion

#region Remote script block
$remoteScript = {
    param([string[]] $MsiList, [bool] $DryRun, [string] $LogFolder)

    $ErrorActionPreference = 'Stop'
    $guidPattern = '^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$'

    function New-Result {
        param($Msi, $DisplayName, $Version, $ProductCode, $Status, $ExitCode, $Message, $LogFile)
        [pscustomobject]@{
            ComputerName = $env:COMPUTERNAME
            Msi          = $Msi
            DisplayName  = $DisplayName
            Version      = $Version
            ProductCode  = $ProductCode
            Status       = $Status
            ExitCode     = $ExitCode
            Message      = $Message
            LogFile      = $LogFile
        }
    }

    # Enumerate installed MSI products (64-bit and 32-bit registry views)
    $uninstallKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $installed = @(
        Get-ItemProperty -Path $uninstallKeys -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -match $guidPattern -and $_.PSObject.Properties['DisplayName'] -and $_.DisplayName } |
            ForEach-Object {
                [pscustomobject]@{
                    ProductCode    = $_.PSChildName.ToUpperInvariant()
                    DisplayName    = $_.DisplayName
                    DisplayVersion = if ($_.PSObject.Properties['DisplayVersion']) { $_.DisplayVersion } else { $null }
                }
            } |
            Sort-Object ProductCode -Unique
    )

    if (-not $DryRun -and -not (Test-Path -LiteralPath $LogFolder)) {
        New-Item -ItemType Directory -Path $LogFolder -Force | Out-Null
    }

    $msiexecMessages = @{
        0    = 'Uninstalled successfully.'
        1605 = 'Product is not installed.'
        1618 = 'Another installation is in progress.'
        1641 = 'Uninstalled; reboot initiated.'
        3010 = 'Uninstalled; reboot required.'
    }

    foreach ($msi in $MsiList) {
        if ($msi -match $guidPattern) {
            $code = '{' + $msi.Trim('{', '}').ToUpperInvariant() + '}'
            $found = @($installed | Where-Object { $_.ProductCode -eq $code })
        }
        else {
            $found = @($installed | Where-Object { $_.DisplayName -like $msi })
        }

        if ($found.Count -eq 0) {
            New-Result -Msi $msi -Status 'NotFound' -Message 'No matching MSI product installed.'
            continue
        }

        foreach ($p in $found) {
            if ($DryRun) {
                New-Result -Msi $msi -DisplayName $p.DisplayName -Version $p.DisplayVersion -ProductCode $p.ProductCode `
                           -Status 'WouldUninstall' -Message 'WhatIf: not uninstalled.'
                continue
            }

            $logFile = Join-Path $LogFolder ("{0}_{1:yyyyMMdd_HHmmss}.log" -f $p.ProductCode.Trim('{', '}'), (Get-Date))
            $msiArgs = @('/x', $p.ProductCode, '/qn', '/norestart', '/l*v', "`"$logFile`"")

            try {
                # Retry a few times if another MSI installation is running (1618)
                $attempt = 0
                do {
                    $attempt++
                    $proc = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $msiArgs -Wait -PassThru -WindowStyle Hidden
                    $exit = $proc.ExitCode
                    if ($exit -eq 1618 -and $attempt -lt 5) { Start-Sleep -Seconds 30 }
                } while ($exit -eq 1618 -and $attempt -lt 5)

                $status = switch ($exit) {
                    0       { 'Success' }
                    1641    { 'SuccessRebootInitiated' }
                    3010    { 'SuccessRebootRequired' }
                    1605    { 'NotInstalled' }
                    default { 'Failed' }
                }
                $msg = if ($msiexecMessages.ContainsKey($exit)) { $msiexecMessages[$exit] } else { "msiexec returned $exit. See log." }

                New-Result -Msi $msi -DisplayName $p.DisplayName -Version $p.DisplayVersion -ProductCode $p.ProductCode `
                           -Status $status -ExitCode $exit -Message $msg -LogFile $logFile
            }
            catch {
                New-Result -Msi $msi -DisplayName $p.DisplayName -Version $p.DisplayVersion -ProductCode $p.ProductCode `
                           -Status 'Error' -Message $_.Exception.Message -LogFile $logFile
            }
        }
    }
}
#endregion

#region Execute
$invokeParams = @{
    ComputerName  = $servers
    Credential    = $Credential
    ScriptBlock   = $remoteScript
    ArgumentList  = @(, $msis) + $dryRun + $RemoteLogFolder
    ThrottleLimit = $ThrottleLimit
    ErrorAction   = 'SilentlyContinue'
    ErrorVariable = 'remoteErrors'
}
if ($UseSSL) { $invokeParams['UseSSL'] = $true }

Write-Host 'Connecting to servers and processing...' -ForegroundColor Cyan
$remoteErrors = @()
$results = @(Invoke-Command @invokeParams |
    Select-Object ComputerName, Msi, DisplayName, Version, ProductCode, Status, ExitCode, Message, LogFile)

# Servers that could not be reached / failed to run the script
foreach ($err in $remoteErrors) {
    $target = $null
    if ($err.PSObject.Properties['TargetObject'] -and $err.TargetObject) { $target = [string]$err.TargetObject }
    if (-not $target -and $err.PSObject.Properties['OriginInfo'] -and $err.OriginInfo) { $target = $err.OriginInfo.PSComputerName }
    $results += [pscustomobject]@{
        ComputerName = $target
        Msi          = ($msis -join '; ')
        DisplayName  = $null
        Version      = $null
        ProductCode  = $null
        Status       = 'ConnectionFailed'
        ExitCode     = $null
        Message      = $err.Exception.Message
        LogFile      = $null
    }
}
#endregion

#region Report
$results | Sort-Object ComputerName, Msi |
    Format-Table ComputerName, Msi, DisplayName, Version, Status, ExitCode -AutoSize | Out-Host

try {
    $results | Export-Csv -LiteralPath $ReportPath -NoTypeInformation -Encoding UTF8 -WhatIf:$false
    Write-Host "Report saved to: $ReportPath" -ForegroundColor Green
}
catch {
    Write-Warning "Could not write report to '$ReportPath': $($_.Exception.Message)"
}

$summary = $results | Group-Object Status | Select-Object @{n='Status';e={$_.Name}}, Count
$summary | Format-Table -AutoSize | Out-Host

if ($results | Where-Object { $_.Status -in 'SuccessRebootRequired', 'SuccessRebootInitiated' }) {
    Write-Warning 'One or more servers require a reboot to complete the uninstall.'
}

# Emit objects so the script can be piped / captured
$results
#endregion
