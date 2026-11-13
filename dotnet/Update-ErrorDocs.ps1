<#
.SYNOPSIS
    Regenerates everything derived from the error catalog in dotnet/MsiRemoteUninstall/Errors.fs:
      * ERROR-CODES.md
      * shared/error-catalog.json          (embedded into the C# .NET Framework tool)
      * the catalog embedded in Uninstall-RemoteMsi.v2.ps1

.DESCRIPTION
    Run this after editing Errors.fs, then rebuild the C# solution (netfx). The unit tests fail if
    any of these files is out of date.
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
[IO.File]::WriteAllText((Join-Path $root 'shared\error-catalog.json'), $json.Replace("`r`n", "`n") + "`n", $utf8)

$scriptPath = Join-Path $root 'Uninstall-RemoteMsi.v2.ps1'
$script = [IO.File]::ReadAllText($scriptPath)
$pattern = "(?s)(\`$script:ErrorCatalogJson = @'\r?\n).*?(\r?\n'@)"
if ($script -notmatch $pattern) { throw 'Catalog block not found in Uninstall-RemoteMsi.v2.ps1' }
$script = [regex]::Replace($script, $pattern, { param($m) $m.Groups[1].Value + $json.Replace("`r`n", "`n") + $m.Groups[2].Value })
[IO.File]::WriteAllText($scriptPath, $script, $utf8)

Write-Host 'ERROR-CODES.md, shared\error-catalog.json and the catalog in Uninstall-RemoteMsi.v2.ps1 were regenerated.' -ForegroundColor Green
