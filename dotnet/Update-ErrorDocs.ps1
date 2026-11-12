<#
.SYNOPSIS
    Regenerates ERROR-CODES.md and the error catalog embedded in
    Uninstall-RemoteMsi.v2.ps1 from dotnet/MsiRemoteUninstall/Errors.fs.

.DESCRIPTION
    Run this after editing Errors.fs. The unit tests fail if either file is out of date.
#>
$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'MsiRemoteUninstall'

$markdown = (& dotnet run --project $project -c Release -- --errors markdown | Out-String)
if ($LASTEXITCODE) { throw "dotnet run failed ($LASTEXITCODE)" }
$json = (& dotnet run --project $project -c Release -- --errors json | Out-String).Trim()
if ($LASTEXITCODE) { throw "dotnet run failed ($LASTEXITCODE)" }

$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $root 'ERROR-CODES.md'), $markdown.Replace("`r`n", "`n"), $utf8)

$scriptPath = Join-Path $root 'Uninstall-RemoteMsi.v2.ps1'
$script = [IO.File]::ReadAllText($scriptPath)
$pattern = "(?s)(\`$script:ErrorCatalogJson = @'\r?\n).*?(\r?\n'@)"
if ($script -notmatch $pattern) { throw 'Catalog block not found in Uninstall-RemoteMsi.v2.ps1' }
$script = [regex]::Replace($script, $pattern, { param($m) $m.Groups[1].Value + $json.Replace("`r`n", "`n") + $m.Groups[2].Value })
[IO.File]::WriteAllText($scriptPath, $script, $utf8)

Write-Host 'ERROR-CODES.md and the catalog in Uninstall-RemoteMsi.v2.ps1 were regenerated.' -ForegroundColor Green
