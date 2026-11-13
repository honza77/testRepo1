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

    With -ListInstalled the script only lists the MSI products installed on the
    servers (optionally filtered with -Filter) and uninstalls nothing. Use it to
    find the exact names / product codes to put into the MSI list.

.PARAMETER ComputerName
    One or more server host names.

.PARAMETER ComputerListPath
    Path to a text file with one server host name per line.

.PARAMETER MsiName
    One or more MSI display names (wildcards allowed) or product code GUIDs.

.PARAMETER MsiListPath
    Path to a text file with one MSI display name or product code per line.

.PARAMETER ListInstalled
    List the MSI products installed on the servers instead of uninstalling.
    -MsiName / -MsiListPath are not needed in this mode.

.PARAMETER Filter
    Used with -ListInstalled. One or more search patterns (case-insensitive).
    A product is listed if its DisplayName, Publisher or ProductCode matches ANY
    of the patterns. How the patterns are interpreted is set by -FilterMode.

.PARAMETER FilterMode
    How -Filter patterns are interpreted:
      Substring (default) - plain text, the field must CONTAIN it. No special characters.
      Wildcard            - PowerShell -like pattern (* ? [a-z]). Must match the WHOLE
                            field, so use e.g. '*agent*' to search anywhere in the name.
      Regex               - .NET regular expression (-match), matches anywhere unless
                            anchored with ^ / $.

.PARAMETER Credential
    Credentials used to connect to the servers. If omitted you will be prompted
    (pop-up window by default, or in the console with -ConsoleCredential).

.PARAMETER UserName
    User name to pre-fill when prompting for credentials, e.g. CONTOSO\admin.
    Only the password is then asked for.

.PARAMETER ConsoleCredential
    Ask for the user name and password as text input in the console instead of
    the Get-Credential pop-up window. Useful over SSH / remote shells or when
    you simply prefer not to have a dialog. The password is read masked and
    kept as a SecureString.

.PARAMETER UseSSL
    Connect to WinRM over HTTPS (port 5986).

.PARAMETER ThrottleLimit
    Maximum number of servers processed in parallel. Default 16.

.PARAMETER ReportPath
    Path of the CSV report. Default: .\MsiUninstallReport_<timestamp>.csv
    (or .\MsiInventory_<timestamp>.csv with -ListInstalled).

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

.EXAMPLE
    .\Uninstall-RemoteMsi.ps1 -ComputerListPath .\servers.txt -ListInstalled -ConsoleCredential -UserName CONTOSO\admin

    Asks only for the password, in the console (no pop-up window).

.EXAMPLE
    .\Uninstall-RemoteMsi.ps1 -ComputerListPath .\servers.txt -ListInstalled -Filter 'contoso','agent'

    Lists installed MSI products whose name, publisher or product code contains
    "contoso" or "agent". Nothing is uninstalled.

.EXAMPLE
    .\Uninstall-RemoteMsi.ps1 -ComputerName SRV01 -ListInstalled -FilterMode Wildcard -Filter 'Microsoft Visual C++ 20?? *'

    Wildcard search.

.EXAMPLE
    .\Uninstall-RemoteMsi.ps1 -ComputerName SRV01 -ListInstalled -FilterMode Regex -Filter '^Contoso (Agent|Monitor)\b'

    Regular expression search.

.NOTES
    Requirements:
      * PowerShell Remoting (WinRM) enabled on the target servers.
      * The account must be a local administrator on the target servers.
    Input files: blank lines and lines starting with '#' are ignored.
#>
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string[]] $ComputerName,

    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $ComputerListPath,

    [string[]] $MsiName,

    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $MsiListPath,

    [switch] $ListInstalled,

    [string[]] $Filter,

    [ValidateSet('Substring', 'Wildcard', 'Regex')]
    [string] $FilterMode = 'Substring',

    [System.Management.Automation.PSCredential]
    [System.Management.Automation.Credential()]
    $Credential = [System.Management.Automation.PSCredential]::Empty,

    [string] $UserName,

    [switch] $ConsoleCredential,

    [switch] $UseSSL,

    [ValidateRange(1, 256)]
    [int] $ThrottleLimit = 16,

    [string] $ReportPath,

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

function ConvertFrom-RemoteError {
    # Turns Invoke-Command errors (unreachable server, access denied, ...) into result rows
    param($ErrorRecords, [hashtable] $ExtraProperties = @{})
    foreach ($err in $ErrorRecords) {
        $target = $null
        if ($err.PSObject.Properties['TargetObject'] -and $err.TargetObject) { $target = [string]$err.TargetObject }
        if (-not $target -and $err.PSObject.Properties['OriginInfo'] -and $err.OriginInfo) { $target = $err.OriginInfo.PSComputerName }
        $row = [ordered]@{ ComputerName = $target }
        foreach ($k in $ExtraProperties.Keys) { $row[$k] = $ExtraProperties[$k] }
        $row['Status']  = 'ConnectionFailed'
        $row['Message'] = $err.Exception.Message
        [pscustomobject]$row
    }
}

function Read-ConsoleCredential {
    # Prompts for credentials as console text input (no GUI pop-up)
    param([string] $DefaultUserName, [string] $Message)
    Write-Host $Message -ForegroundColor Cyan
    $user = $DefaultUserName
    if ($user) {
        Write-Host "User name: $user"
    }
    else {
        $user = (Read-Host -Prompt 'User name (e.g. DOMAIN\user)').Trim()
    }
    if (-not $user) { throw 'No user name supplied.' }

    $password = Read-Host -Prompt "Password for $user" -AsSecureString
    if (-not $password -or $password.Length -eq 0) { throw 'No password supplied.' }

    New-Object System.Management.Automation.PSCredential ($user, $password)
}

function Save-Report {
    param($Rows, [string] $Path)
    try {
        $Rows | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8 -WhatIf:$false
        Write-Host "Report saved to: $Path" -ForegroundColor Green
    }
    catch {
        Write-Warning "Could not write report to '$Path': $($_.Exception.Message)"
    }
}
#endregion

#region Build input lists
$servers = @(Merge-List -Inline $ComputerName -FilePath $ComputerListPath)
$msis    = @(Merge-List -Inline $MsiName      -FilePath $MsiListPath)

if ($servers.Count -eq 0) { throw 'No servers specified. Use -ComputerName and/or -ComputerListPath.' }
if ($msis.Count -eq 0 -and -not $ListInstalled) { throw 'No MSIs specified. Use -MsiName and/or -MsiListPath (or -ListInstalled to list installed MSIs).' }
if ($Filter -and -not $ListInstalled) { Write-Warning '-Filter is only used together with -ListInstalled; ignoring it.' }

# Validate filter patterns up front so a typo fails before connecting anywhere
if ($ListInstalled -and $Filter) {
    foreach ($f in $Filter) {
        if (-not $f -or -not $f.Trim()) { continue }
        try {
            switch ($FilterMode) {
                'Regex'    { [void][regex]::new($f.Trim(), 'IgnoreCase') }
                'Wildcard' { [void][System.Management.Automation.WildcardPattern]::new($f.Trim(), 'IgnoreCase').IsMatch('') }
            }
        }
        catch {
            $ex = $_.Exception
            if ($ex.InnerException) { $ex = $ex.InnerException }
            throw "Invalid $FilterMode filter '$f': $($ex.Message)"
        }
    }
}

if (-not $ReportPath) {
    $prefix = if ($ListInstalled) { 'MsiInventory' } else { 'MsiUninstallReport' }
    $ReportPath = Join-Path -Path (Get-Location) -ChildPath ("{0}_{1:yyyyMMdd_HHmmss}.csv" -f $prefix, (Get-Date))
}

if ($Credential -eq [System.Management.Automation.PSCredential]::Empty) {
    $credMessage = 'Enter credentials with local admin rights on the target servers'
    if ($ConsoleCredential) {
        $Credential = Read-ConsoleCredential -DefaultUserName $UserName -Message $credMessage
    }
    else {
        $getCredParams = @{ Message = $credMessage }
        if ($UserName) { $getCredParams['UserName'] = $UserName }
        $Credential = Get-Credential @getCredParams
    }
    if (-not $Credential) { throw 'No credentials supplied.' }
}
elseif ($UserName -or $ConsoleCredential) {
    Write-Warning '-Credential was supplied; -UserName / -ConsoleCredential are ignored.'
}

$connParams = @{
    ComputerName  = $servers
    Credential    = $Credential
    ThrottleLimit = $ThrottleLimit
    ErrorAction   = 'SilentlyContinue'
}
if ($UseSSL) { $connParams['UseSSL'] = $true }

#region List mode
if ($ListInstalled) {
    $filters = @($Filter | Where-Object { $_ -and $_.Trim() } | ForEach-Object { $_.Trim() })

    Write-Host ''
    Write-Host ("Servers ({0}): {1}" -f $servers.Count, ($servers -join ', ')) -ForegroundColor Cyan
    if ($filters.Count) { Write-Host ("Filter ({0}, any of): {1}" -f $FilterMode.ToLower(), ($filters -join ', ')) -ForegroundColor Cyan }
    else                { Write-Host 'Filter: none (listing all MSI products)' -ForegroundColor Cyan }
    Write-Host ''

    $listScript = {
        param([string[]] $Filters, [string] $Mode)
        $guidPattern = '^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$'
        $keys = @(
            @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*';             Arch = '64-bit' },
            @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'; Arch = '32-bit' }
        )
        $products = foreach ($k in $keys) {
            Get-ItemProperty -Path $k.Path -ErrorAction SilentlyContinue |
                Where-Object { $_.PSChildName -match $guidPattern -and $_.PSObject.Properties['DisplayName'] -and $_.DisplayName } |
                ForEach-Object {
                    $get = { param($o, $n) if ($o.PSObject.Properties[$n]) { $o.$n } else { $null } }
                    [pscustomobject]@{
                        ComputerName = $env:COMPUTERNAME
                        DisplayName  = $_.DisplayName
                        Version      = & $get $_ 'DisplayVersion'
                        Publisher    = & $get $_ 'Publisher'
                        InstallDate  = & $get $_ 'InstallDate'
                        Architecture = $k.Arch
                        ProductCode  = $_.PSChildName.ToUpperInvariant()
                    }
                }
        }
        $products = $products | Sort-Object ProductCode -Unique

        if ($Filters -and $Filters.Count) {
            $products = $products | Where-Object {
                $p = $_
                $hit = $false
                foreach ($f in $Filters) {
                    foreach ($field in @($p.DisplayName, $p.Publisher, $p.ProductCode)) {
                        if (-not $field) { continue }
                        $text = [string]$field
                        $isMatch = switch ($Mode) {
                            'Wildcard' { $text -like $f }     # -like / -match are case-insensitive
                            'Regex'    { $text -match $f }
                            default    { $text.IndexOf($f, [StringComparison]::OrdinalIgnoreCase) -ge 0 }
                        }
                        if ($isMatch) { $hit = $true; break }
                    }
                    if ($hit) { break }
                }
                $hit
            }
        }
        $products | Sort-Object DisplayName
    }

    Write-Host 'Connecting to servers and collecting installed MSIs...' -ForegroundColor Cyan
    $remoteErrors = @()
    $inventory = @(Invoke-Command @connParams -ScriptBlock $listScript -ArgumentList $filters, $FilterMode -ErrorVariable remoteErrors |
        Select-Object ComputerName, DisplayName, Version, Publisher, InstallDate, Architecture, ProductCode)

    $failed = @(ConvertFrom-RemoteError -ErrorRecords $remoteErrors)

    if ($inventory.Count) {
        $inventory | Sort-Object ComputerName, DisplayName |
            Format-Table ComputerName, DisplayName, Version, Publisher, ProductCode -AutoSize | Out-Host
    }
    else {
        Write-Host 'No matching MSI products found.' -ForegroundColor Yellow
    }
    if ($failed.Count) {
        Write-Warning ("{0} server(s) could not be queried:" -f $failed.Count)
        $failed | Format-Table ComputerName, Message -AutoSize -Wrap | Out-Host
    }

    $inventory | Group-Object ComputerName |
        Select-Object @{n='ComputerName';e={$_.Name}}, @{n='MatchingMsis';e={$_.Count}} |
        Format-Table -AutoSize | Out-Host

    $reportRows = @($inventory) + @($failed | Select-Object ComputerName, @{n='DisplayName';e={"ERROR: $($_.Message)"}},
        Version, Publisher, InstallDate, Architecture, ProductCode)
    if ($reportRows.Count) { Save-Report -Rows $reportRows -Path $ReportPath }

    $inventory
    return
}
#endregion

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
Write-Host 'Connecting to servers and processing...' -ForegroundColor Cyan
$remoteErrors = @()
$results = @(Invoke-Command @connParams -ScriptBlock $remoteScript -ArgumentList (@(, $msis) + $dryRun + $RemoteLogFolder) -ErrorVariable remoteErrors |
    Select-Object ComputerName, Msi, DisplayName, Version, ProductCode, Status, ExitCode, Message, LogFile)

# Servers that could not be reached / failed to run the script
$results += @(ConvertFrom-RemoteError -ErrorRecords $remoteErrors -ExtraProperties @{ Msi = ($msis -join '; ') } |
    Select-Object ComputerName, Msi, DisplayName, Version, ProductCode, Status, ExitCode, Message, LogFile)
#endregion

#region Report
$results | Sort-Object ComputerName, Msi |
    Format-Table ComputerName, Msi, DisplayName, Version, Status, ExitCode -AutoSize | Out-Host

Save-Report -Rows $results -Path $ReportPath

$summary = $results | Group-Object Status | Select-Object @{n='Status';e={$_.Name}}, Count
$summary | Format-Table -AutoSize | Out-Host

if ($results | Where-Object { $_.Status -in 'SuccessRebootRequired', 'SuccessRebootInitiated' }) {
    Write-Warning 'One or more servers require a reboot to complete the uninstall.'
}

# Emit objects so the script can be piped / captured
$results
#endregion
