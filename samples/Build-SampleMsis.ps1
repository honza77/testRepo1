<#
.SYNOPSIS
    Builds the HelloWorld sample apps and the sample MSI packages used to test
    MsiRemoteUninstall (F#, C#) and Uninstall-RemoteMsi*.ps1.

.DESCRIPTION
    Requirements (Windows): .NET SDK 8 (dotnet). WiX is restored automatically from
    NuGet (WixToolset.Sdk) - no separate WiX installation is needed.

    Output:
      out\apps\...                 the built HelloWorld apps
      out\msi\*.msi                the sample packages
      out\msi\packages.txt         list file for  --install-list
      out\msi\uninstall.txt        list file for  --msi-list / -MsiListPath

    Packages:
      Contoso.HelloWorld-1.0.0.msi       Contoso HelloWorld 1.0.0 (x64)           normal
      Contoso.HelloWorld-2.0.0.msi       Contoso HelloWorld 2.0.0 (x64)           major upgrade of 1.0.0
      Fabrikam.HelloWorld.x86.msi        Fabrikam HelloWorld (x86), F# app        32-bit (WOW6432Node)
      Contoso.RebootRequired.msi         Contoso HelloWorld RebootRequired        uninstall -> 3010
      Contoso.FailingUninstall.msi       Contoso HelloWorld FailingUninstall      uninstall -> 1603
      Contoso.SlowUninstall.msi          Contoso HelloWorld SlowUninstall         uninstall ~150 s

.EXAMPLE
    .\Build-SampleMsis.ps1
#>
[CmdletBinding()]
param(
    [string] $OutDir = (Join-Path $PSScriptRoot 'out'),
    [string] $Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

function Invoke-Dotnet {
    param([string[]] $Arguments)
    Write-Host ("dotnet {0}" -f ($Arguments -join ' ')) -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

$apps = Join-Path $OutDir 'apps'
$msi  = Join-Path $OutDir 'msi'
New-Item -ItemType Directory -Path $apps, $msi -Force | Out-Null

# ---- 1. apps (C# app in two versions, F# app 32-bit)
$appDirs = @{
    'cs-1.0.0' = Join-Path $apps 'HelloWorld.CSharp-1.0.0'
    'cs-2.0.0' = Join-Path $apps 'HelloWorld.CSharp-2.0.0'
    'fs-1.0.0' = Join-Path $apps 'HelloWorld.FSharp-1.0.0'
}
Invoke-Dotnet @('build', (Join-Path $PSScriptRoot 'HelloWorld.CSharp'), '-c', $Configuration, '-o', $appDirs['cs-1.0.0'], '-p:Version=1.0.0', '--no-incremental')
Invoke-Dotnet @('build', (Join-Path $PSScriptRoot 'HelloWorld.CSharp'), '-c', $Configuration, '-o', $appDirs['cs-2.0.0'], '-p:Version=2.0.0', '--no-incremental')
Invoke-Dotnet @('build', (Join-Path $PSScriptRoot 'HelloWorld.FSharp'), '-c', $Configuration, '-o', $appDirs['fs-1.0.0'], '-p:Version=1.0.0', '--no-incremental')

# ---- 2. MSI variants
$variants = @(
    @{ Out = 'Contoso.HelloWorld-1.0.0';  Name = 'Contoso HelloWorld';                  Maker = 'Contoso';  Version = '1.0.0'; Platform = 'x64'; Behavior = 'Normal';           App = 'cs-1.0.0'; Upgrade = '{6F1D0C2E-6A55-4C8E-9C0B-6B1D5B3E1A01}' }
    @{ Out = 'Contoso.HelloWorld-2.0.0';  Name = 'Contoso HelloWorld';                  Maker = 'Contoso';  Version = '2.0.0'; Platform = 'x64'; Behavior = 'Normal';           App = 'cs-2.0.0'; Upgrade = '{6F1D0C2E-6A55-4C8E-9C0B-6B1D5B3E1A01}' }
    @{ Out = 'Fabrikam.HelloWorld.x86';   Name = 'Fabrikam HelloWorld (x86)';           Maker = 'Fabrikam'; Version = '1.0.0'; Platform = 'x86'; Behavior = 'Normal';           App = 'fs-1.0.0'; Upgrade = '{6F1D0C2E-6A55-4C8E-9C0B-6B1D5B3E1A02}' }
    @{ Out = 'Contoso.RebootRequired';    Name = 'Contoso HelloWorld RebootRequired';   Maker = 'Contoso';  Version = '1.0.0'; Platform = 'x64'; Behavior = 'RebootRequired';   App = 'cs-1.0.0'; Upgrade = '{6F1D0C2E-6A55-4C8E-9C0B-6B1D5B3E1A03}' }
    @{ Out = 'Contoso.FailingUninstall';  Name = 'Contoso HelloWorld FailingUninstall'; Maker = 'Contoso';  Version = '1.0.0'; Platform = 'x64'; Behavior = 'FailingUninstall'; App = 'cs-1.0.0'; Upgrade = '{6F1D0C2E-6A55-4C8E-9C0B-6B1D5B3E1A04}' }
    @{ Out = 'Contoso.SlowUninstall';     Name = 'Contoso HelloWorld SlowUninstall';    Maker = 'Contoso';  Version = '1.0.0'; Platform = 'x64'; Behavior = 'SlowUninstall';    App = 'cs-1.0.0'; Upgrade = '{6F1D0C2E-6A55-4C8E-9C0B-6B1D5B3E1A05}' }
)

$wixproj = Join-Path $PSScriptRoot 'Installer\HelloWorld.Installer.wixproj'
foreach ($v in $variants) {
    Write-Host ''
    Write-Host ("Building {0}.msi ({1} {2}, {3}, {4})" -f $v.Out, $v.Name, $v.Version, $v.Platform, $v.Behavior) -ForegroundColor Cyan
    Invoke-Dotnet @(
        'build', $wixproj, '-c', $Configuration, '-o', $msi, '--no-incremental',
        "-p:OutputName=$($v.Out)",
        "-p:Platform=$($v.Platform)",
        "-p:PkgName=$($v.Name)",
        "-p:PkgManufacturer=$($v.Maker)",
        "-p:PkgVersion=$($v.Version)",
        "-p:PkgUpgradeCode=$($v.Upgrade)",
        "-p:PkgBehavior=$($v.Behavior)",
        "-p:PkgAppDir=$($appDirs[$v.App])"
    )
}

# ---- 3. list files for the tools
# Install order: 2.0.0 is left out on purpose - install it later to test a major upgrade.
@('# Sample packages for:  MsiRemoteUninstall --install-list packages.txt -s <this folder>') +
    ($variants | Where-Object { $_.Out -ne 'Contoso.HelloWorld-2.0.0' } | ForEach-Object { "$($_.Out).msi" }) |
    Set-Content -Path (Join-Path $msi 'packages.txt') -Encoding UTF8

@('# Sample products for:  MsiRemoteUninstall --msi-list uninstall.txt   /   Uninstall-RemoteMsi.ps1 -MsiListPath uninstall.txt',
  '# Contoso HelloWorld FailingUninstall is NOT listed: remove it separately with  -p FORCE_UNINSTALL=1',
  'Contoso HelloWorld',
  'Fabrikam HelloWorld*',
  'Contoso HelloWorld RebootRequired',
  'Contoso HelloWorld SlowUninstall') |
    Set-Content -Path (Join-Path $msi 'uninstall.txt') -Encoding UTF8

Write-Host ''
Write-Host "Done. Packages in $msi :" -ForegroundColor Green
Get-ChildItem $msi -Filter *.msi | Format-Table Name, Length -AutoSize
