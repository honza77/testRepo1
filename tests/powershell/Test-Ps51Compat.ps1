<#
.SYNOPSIS
    Static check that scripts only use syntax, cmdlets, parameters, variables and .NET APIs that
    exist in Windows PowerShell 5.1.

.DESCRIPTION
    Uses the PowerShell parser (AST), so it runs in Windows PowerShell 5.1 and in PowerShell 7.
    It is a lightweight stand-in for PSScriptAnalyzer's PSUseCompatibleSyntax/Commands rules and
    catches the common PowerShell 7-only constructs:
      ?: ?? ??= ?. ?[] && || clean{}, ForEach-Object -Parallel and other 6/7-only parameters,
      $IsWindows / $IsLinux / $PSStyle, .NET Core-only APIs, and non-ASCII characters in files
      without a UTF-8 BOM (Windows PowerShell 5.1 reads those as ANSI).
    Exit code = number of problems found.

.EXAMPLE
    .\tests\powershell\Test-Ps51Compat.ps1 -Path (Get-ChildItem -Recurse -Filter *.ps1).FullName
#>
#Requires -Version 5.1
param([Parameter(Mandatory)] [string[]] $Path)
$newParams = @{
    'ForEach-Object'    = 'Parallel', 'ThrottleLimit', 'TimeoutSeconds', 'AsJob'
    'ConvertFrom-Json'  = 'AsHashtable', 'Depth', 'NoEnumerate'
    'ConvertTo-Json'    = 'AsArray', 'EnumsAsStrings', 'EscapeHandling'
    'Get-Content'       = 'AsByteStream'
    'Set-Content'       = 'AsByteStream'
    'Join-Path'         = 'AdditionalChildPath'
    'Split-Path'        = 'LeafBase', 'Extension'
    'Invoke-WebRequest' = 'SkipCertificateCheck', 'Authentication', 'Token', 'SslProtocol', 'MaximumRetryCount', 'Resume', 'SkipHttpErrorCheck'
    'Invoke-RestMethod' = 'SkipCertificateCheck', 'Authentication', 'Token', 'SslProtocol', 'MaximumRetryCount', 'StatusCodeVariable', 'SkipHttpErrorCheck'
    'Select-Object'     = 'SkipIndex'
    'Test-Connection'   = 'TargetName', 'TcpPort', 'Ping', 'Traceroute', 'MtuSize', 'Repeat'
    'Export-Csv'        = 'IncludeTypeInformation', 'QuoteFields', 'UseQuotes'
    'Start-Process'     = 'Environment'
    'Sort-Object'       = 'Stable', 'Top', 'Bottom'
    'Get-Date'          = 'AsUTC', 'UnixTimeSeconds'
}
$newCommands = 'Get-Error', 'Test-Json', 'ConvertFrom-Markdown', 'Join-String', 'Get-Uptime', 'ConvertTo-CliXml'
$newVariables = 'IsWindows', 'IsLinux', 'IsMacOS', 'IsCoreCLR', 'PSStyle', 'PSNativeCommandArgumentPassing'
$L = [System.Management.Automation.Language.Ast]
$script:problems = 0
function Report($file, $line, $msg) {
    $script:problems++
    Write-Host ("{0}:{1}: {2}" -f (Split-Path $file -Leaf), $line, $msg) -ForegroundColor Yellow
}

foreach ($file in $Path) {
    $full = (Resolve-Path $file).Path
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($full, [ref]$tokens, [ref]$errors)
    foreach ($e in $errors) { Report $file $e.Extent.StartLineNumber "PARSE ERROR: $($e.Message)" }

    # non-ASCII characters (Windows PowerShell 5.1 reads BOM-less files as ANSI)
    $bytes = [IO.File]::ReadAllBytes($full)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $lineNo = 1
    foreach ($b in $bytes) {
        if ($b -eq 10) { $lineNo++ }
        elseif ($b -gt 127 -and -not $hasBom) { Report $file $lineNo 'non-ASCII byte in a file without UTF-8 BOM'; break }
    }

    # syntax new in PowerShell 6/7
    $ast.FindAll({ param($n) $n.GetType().Name -eq 'TernaryExpressionAst' }, $true) | ForEach-Object { Report $file $_.Extent.StartLineNumber 'ternary operator ?: (PS 7)' }
    $ast.FindAll({ param($n) $n.GetType().Name -eq 'PipelineChainAst' }, $true) | ForEach-Object { Report $file $_.Extent.StartLineNumber '&& / || pipeline chain (PS 7)' }
    $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.BinaryExpressionAst] -and "$($n.Operator)" -eq 'QuestionQuestion' }, $true) |
        ForEach-Object { Report $file $_.Extent.StartLineNumber '?? operator (PS 7)' }
    $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and "$($n.Operator)" -eq 'QuestionQuestionEquals' }, $true) |
        ForEach-Object { Report $file $_.Extent.StartLineNumber '??= operator (PS 7)' }
    $ast.FindAll({ param($n) ($n -is [System.Management.Automation.Language.MemberExpressionAst] -or $n -is [System.Management.Automation.Language.IndexExpressionAst]) -and $n.NullConditional }, $true) |
        ForEach-Object { Report $file $_.Extent.StartLineNumber '?. / ?[] null-conditional access (PS 7)' }
    $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.NamedBlockAst] -and "$($n.BlockKind)" -eq 'Clean' }, $true) |
        ForEach-Object { Report $file $_.Extent.StartLineNumber 'clean block (PS 7.3)' }

    # commands / parameters / variables
    foreach ($cmd in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
        $name = $cmd.GetCommandName()
        if (-not $name) { continue }
        if ($name -in $newCommands) { Report $file $cmd.Extent.StartLineNumber "command $name does not exist in 5.1" }
        if ($newParams.ContainsKey($name)) {
            foreach ($p in $cmd.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] }) {
                foreach ($np in $newParams[$name]) {
                    if ($p.ParameterName -eq $np) {
                        Report $file $p.Extent.StartLineNumber "$name -$($p.ParameterName) does not exist in 5.1"
                    }
                }
            }
        }
    }
    $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] -and $n.VariablePath.UserPath -in $newVariables }, $true) |
        ForEach-Object { Report $file $_.Extent.StartLineNumber "`$$($_.VariablePath.UserPath) does not exist in 5.1" }

    # .NET APIs that exist only in .NET Core / .NET 5+ (Windows PowerShell 5.1 runs on .NET Framework 4.x)
    # (.Split('x', options) binds to the .NET Core-only Split(string, StringSplitOptions) overload)
    $netCore = "\.Split\(\s*'[^']*'\s*,|TrimEntries|\[System\.HashCode\]|\.ReplaceLineEndings|ToHexString|::GetRelativePath|ReadAllTextAsync|System\.Text\.Json|\[System\.Range\]|\[System\.Index\]"
    $i = 0
    foreach ($l in [IO.File]::ReadAllLines($full)) {
        $i++
        if ($l.TrimStart().StartsWith('#') -or $l.Contains('$netCore')) { continue }   # comments / this check itself
        if ($l -match $netCore) { Report $file $i ".NET Core-only API: $($Matches[0])" }
    }

    # classes work in 5.1 but have quirks worth knowing
    $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.TypeDefinitionAst] }, $true) |
        ForEach-Object { Write-Host ("{0}:{1}: note: PowerShell class '{2}'" -f (Split-Path $file -Leaf), $_.Extent.StartLineNumber, $_.Name) -ForegroundColor DarkGray }
}
Write-Host "$script:problems problem(s) found."
exit $script:problems
