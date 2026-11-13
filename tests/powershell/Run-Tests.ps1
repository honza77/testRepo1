<#
.SYNOPSIS
    Tests Uninstall-RemoteMsi.ps1 and Uninstall-RemoteMsi.v2.ps1 without any server.

.DESCRIPTION
    Every scenario runs in a new PowerShell process (the same PowerShell that runs this file,
    or -Shell). Mocks.ps1 replaces Invoke-Command, Get-ItemProperty and Start-Process there,
    so the scripts' real "remote" code, matching, msiexec exit-code handling, reports and
    process exit codes run locally against five fake products.

    Run it in Windows PowerShell 5.1 to verify 5.1 compatibility:
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\powershell\Run-Tests.ps1
    or in PowerShell 7 (Windows, Linux, macOS):
        pwsh -NoProfile -File ./tests/powershell/Run-Tests.ps1

    It also runs Test-Ps51Compat.ps1 on all scripts of the repository.
    Exit code = number of failed checks.

.PARAMETER Shell
    PowerShell executable for the scenarios. Default: the one running this script.
#>
#Requires -Version 5.1
[CmdletBinding()]
param([string] $Shell)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path (Split-Path $here -Parent) -Parent
$v1 = Join-Path $root 'Uninstall-RemoteMsi.ps1'
$v2 = Join-Path $root 'Uninstall-RemoteMsi.v2.ps1'
if (-not $Shell) {
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        $Shell = Join-Path $PSHOME 'powershell.exe'           # Windows PowerShell 5.1
    }
    else {
        $exe = if ([Environment]::OSVersion.Platform -eq 'Win32NT') { 'pwsh.exe' } else { 'pwsh' }
        $Shell = Join-Path $PSHOME $exe
        if (-not (Test-Path -LiteralPath $Shell)) { $Shell = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source }
    }
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('mru-ps-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

$script:passed = 0
$script:failed = 0

Write-Host ("PowerShell under test: {0}" -f $Shell) -ForegroundColor Cyan
Write-Host ("Work folder: {0}" -f $work)

# Runs $Body (PowerShell code; V1 / V2 are replaced by the script paths) in a new process.
# Returns @{ Exit; Rows; Calls }.
function Invoke-Scenario {
    param([string] $Body)
    $id = [guid]::NewGuid().ToString('N')
    $resultFile = Join-Path $work "$id.json"
    $scenarioFile = Join-Path $work "$id.ps1"
    $body = $Body.Replace('V2 ', "& '$v2' ").Replace('V1 ', "& '$v1' ")
    $code = @"
`$ErrorActionPreference = 'Continue'
Set-Location -LiteralPath '$work'
. '$(Join-Path $here 'Mocks.ps1')'
`$c = New-Object System.Management.Automation.PSCredential ('u', (ConvertTo-SecureString 'p' -AsPlainText -Force))
`$log = Join-Path '$work' 'logs'
`$r = @(& { $body } 6>`$null)
`$exit = `$LASTEXITCODE
`$rows = @(`$r | Where-Object { `$_ -and `$_.PSObject.Properties['Status'] -or `$_.PSObject.Properties['DisplayName'] } |
          Select-Object Msi, DisplayName, Status, ExitCode, ErrorCode)
`$result = New-Object PSObject -Property @{ Exit = `$exit; Rows = `$rows; Calls = `$global:MsiexecCalls.Count }
ConvertTo-Json -InputObject `$result -Depth 4 | Set-Content -LiteralPath '$resultFile' -Encoding UTF8
"@
    Set-Content -LiteralPath $scenarioFile -Value $code -Encoding UTF8
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $null = & $Shell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $scenarioFile 2>&1
    $ErrorActionPreference = $old
    if (-not (Test-Path -LiteralPath $resultFile)) { throw "Scenario produced no result: $Body" }
    Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
}

function Expect {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Get-Status {
    param($Result, [string] $DisplayName)
    $row = @($Result.Rows | Where-Object { $_.DisplayName -eq $DisplayName }) | Select-Object -First 1
    if ($row) { $row.Status } else { '<no row>' }
}

function Test-Case {
    param([string] $Name, [string] $Body, [scriptblock] $Check)
    try {
        $res = Invoke-Scenario -Body $Body
        & $Check $res
        $script:passed++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    }
    catch {
        $script:failed++
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        Write-Host "        $($_.Exception.Message)" -ForegroundColor Red
    }
}

# ------------------------------------------------------------------ v2: help and input errors
Write-Host ''
Write-Host 'Uninstall-RemoteMsi.v2.ps1' -ForegroundColor Cyan
Test-Case 'ExplainError 1603 -> exit 0' 'V2 -ExplainError 1603' { param($r) Expect ($r.Exit -eq 0) "exit $($r.Exit)" }
Test-Case 'ExplainError unknown -> exit 2' 'V2 -ExplainError NOPE' { param($r) Expect ($r.Exit -eq 2) "exit $($r.Exit)" }
Test-Case 'ListErrorCodes -> exit 0' 'V2 -ListErrorCodes' { param($r) Expect ($r.Exit -eq 0) "exit $($r.Exit)" }
Test-Case 'no servers (MRU102) -> exit 2' 'V2 -MsiName x' { param($r) Expect ($r.Exit -eq 2) "exit $($r.Exit)" }
Test-Case 'missing list file (MRU104) -> exit 2' 'V2 -ComputerListPath nope.txt -MsiName x' { param($r) Expect ($r.Exit -eq 2) "exit $($r.Exit)" }
Test-Case 'invalid regex filter (MRU106) -> exit 2' "V2 -ComputerName a -ListInstalled -FilterMode Regex -Filter '(x'" { param($r) Expect ($r.Exit -eq 2) "exit $($r.Exit)" }
Test-Case 'invalid MSI wildcard (MRU107) -> exit 2' "V2 -ComputerName a -MsiName '[abc'" { param($r) Expect ($r.Exit -eq 2) "exit $($r.Exit)" }
Test-Case 'console credential without console (MRU204) -> exit 3' 'V2 -ComputerName a -MsiName x -ConsoleCredential' { param($r) Expect ($r.Exit -eq 3) "exit $($r.Exit)" }

# ------------------------------------------------------------------ v2: preflight
Test-Case 'unresolvable server (MRU301) -> exit 5, ConnectionFailed row' 'V2 -ComputerName no-such-host.invalid -MsiName x -Credential $c -Confirm:$false' {
    param($r)
    Expect ($r.Exit -eq 5) "exit $($r.Exit)"
    $row = @($r.Rows)[0]
    Expect ($row.Status -eq 'ConnectionFailed' -and $row.ErrorCode -eq 'MRU301') "row: $($row.Status) $($row.ErrorCode)"
}

# ------------------------------------------------------------------ v2: uninstall outcomes (mocked server)
Test-Case 'uninstall: every outcome, exit 1' "V2 -ComputerName srv01 -MsiName 'Contoso HelloWorld*','Fabrikam*','{AAAAAAAA-0000-0000-0000-000000000001}','Missing App' -Credential `$c -Confirm:`$false -SkipPreflight -RemoteLogFolder `$log -TimeoutMinutes 1" {
    param($r)
    Expect ($r.Exit -eq 1) "exit $($r.Exit)"
    Expect ((Get-Status $r 'Contoso HelloWorld') -eq 'Success') 'Contoso HelloWorld'
    Expect ((Get-Status $r 'Contoso HelloWorld RebootRequired') -eq 'SuccessRebootRequired') 'RebootRequired'
    Expect ((Get-Status $r 'Contoso HelloWorld FailingUninstall') -eq 'Failed') 'FailingUninstall'
    Expect ((Get-Status $r 'Contoso HelloWorld SlowUninstall') -eq 'ExecutionError') 'SlowUninstall'
    Expect ((Get-Status $r 'Fabrikam HelloWorld (x86)') -eq 'Success') 'Fabrikam (32-bit)'
    $codes = ($r.Rows | ForEach-Object { $_.ErrorCode } | Where-Object { $_ } | Sort-Object) -join ','
    Expect ($codes -eq 'MRU403,MSI1603,MSI3010') "codes: $codes"
    $notFound = @($r.Rows | Where-Object { $_.Msi -eq 'Missing App' -and $_.Status -eq 'NotFound' })
    Expect ($notFound.Count -eq 1) 'Missing App should be NotFound'
    Expect ($r.Calls -eq 5) "msiexec calls: $($r.Calls) (product code entry must not uninstall twice)"
}
Test-Case 'uninstall: only reboot required -> exit 10' "V2 -ComputerName srv01 -MsiName '*RebootRequired' -Credential `$c -Confirm:`$false -SkipPreflight -RemoteLogFolder `$log" {
    param($r) Expect ($r.Exit -eq 10) "exit $($r.Exit)"
}
Test-Case 'uninstall: -WhatIf runs no msiexec' "V2 -ComputerName srv01,srv02 -MsiName 'Contoso*' -Credential `$c -WhatIf -SkipPreflight -RemoteLogFolder `$log" {
    param($r)
    Expect ($r.Exit -eq 0) "exit $($r.Exit)"
    Expect ($r.Calls -eq 0) "msiexec calls: $($r.Calls)"
    $statuses = (@($r.Rows) | ForEach-Object { $_.Status } | Sort-Object -Unique) -join ','
    Expect ($statuses -eq 'WouldUninstall') "statuses: $statuses"
    Expect (@($r.Rows).Count -eq 8) "rows: $(@($r.Rows).Count)"
}

# ------------------------------------------------------------------ v2: list / filters
$listCases = @(
    @{ Mode = 'Substring'; Filter = 'hello';                                 Expected = 5 }
    @{ Mode = 'Wildcard';  Filter = '*(x86)';                                Expected = 1 }
    @{ Mode = 'Regex';     Filter = '^contoso.*(reboot|slow)';               Expected = 2 }
    @{ Mode = 'Substring'; Filter = 'AAAAAAAA-0000-0000-0000-000000000003';  Expected = 1 }
)
foreach ($lc in $listCases) {
    $expected = $lc.Expected
    Test-Case ("list {0} '{1}' -> {2} product(s)" -f $lc.Mode, $lc.Filter, $expected) ("V2 -ComputerName srv01 -ListInstalled -FilterMode {0} -Filter '{1}' -Credential `$c -SkipPreflight" -f $lc.Mode, $lc.Filter) ({
        param($r)
        Expect ($r.Exit -eq 0) "exit $($r.Exit)"
        Expect (@($r.Rows).Count -eq $expected) "found $(@($r.Rows).Count)"
    }.GetNewClosure())
}

# ------------------------------------------------------------------ v1
Write-Host ''
Write-Host 'Uninstall-RemoteMsi.ps1' -ForegroundColor Cyan
Test-Case 'uninstall: outcomes' "V1 -ComputerName srv01 -MsiName 'Contoso HelloWorld*','Fabrikam*','Missing App' -Credential `$c -Confirm:`$false -RemoteLogFolder `$log" {
    param($r)
    Expect ((Get-Status $r 'Contoso HelloWorld') -eq 'Success') 'Contoso HelloWorld'
    Expect ((Get-Status $r 'Contoso HelloWorld RebootRequired') -eq 'SuccessRebootRequired') 'RebootRequired'
    Expect ((Get-Status $r 'Contoso HelloWorld FailingUninstall') -eq 'Failed') 'FailingUninstall'
    Expect ((Get-Status $r 'Fabrikam HelloWorld (x86)') -eq 'Success') 'Fabrikam (32-bit)'
    Expect (@($r.Rows | Where-Object { $_.Status -eq 'NotFound' }).Count -eq 1) 'Missing App should be NotFound'
}
Test-Case 'uninstall: -WhatIf runs no msiexec' "V1 -ComputerName srv01 -MsiName 'Contoso*' -Credential `$c -WhatIf -RemoteLogFolder `$log" {
    param($r) Expect ($r.Calls -eq 0) "msiexec calls: $($r.Calls)"
}
Test-Case 'list wildcard' "V1 -ComputerName srv01 -ListInstalled -FilterMode Wildcard -Filter '*hello*' -Credential `$c" {
    param($r) Expect (@($r.Rows).Count -eq 5) "found $(@($r.Rows).Count)"
}

# ------------------------------------------------------------------ static 5.1 compatibility check
Write-Host ''
Write-Host 'Static Windows PowerShell 5.1 compatibility check' -ForegroundColor Cyan
$scripts = Get-ChildItem -LiteralPath $root -Recurse -Filter *.ps1 |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|out)[\\/]' } | ForEach-Object { $_.FullName }
& (Join-Path $here 'Test-Ps51Compat.ps1') -Path $scripts
$compatProblems = $LASTEXITCODE
if ($compatProblems -eq 0) { $script:passed++; Write-Host '  PASS  no PowerShell 6/7-only constructs' -ForegroundColor Green }
else { $script:failed++; Write-Host "  FAIL  $compatProblems compatibility problem(s)" -ForegroundColor Red }

Write-Host ''
$color = if ($script:failed -eq 0) { 'Green' } else { 'Red' }
Write-Host ("{0} passed, {1} failed" -f $script:passed, $script:failed) -ForegroundColor $color
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
exit $script:failed
