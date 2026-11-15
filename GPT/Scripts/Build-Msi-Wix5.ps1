
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$ProductName,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Manufacturer = "MyCompany",

    [string]$RuntimeIdentifier = "win-x64",

    [string]$OutputDirectory,

    [bool]$SelfContained = $true,

    [switch]$KeepBuildFiles
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$WixSdkVersion = "5.0.2"

function ConvertTo-XmlAttribute {
    param([string]$Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

function New-DeterministicGuid {
    param([string]$Text)

    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        $hash = $md5.ComputeHash($bytes)
    }
    finally {
        $md5.Dispose()
    }

    # Mark as a name-based UUID (version 3), with the RFC variant bits.
    $hash[7] = ($hash[7] -band 0x0F) -bor 0x30
    $hash[8] = ($hash[8] -band 0x3F) -bor 0x80

    return ([Guid]::new($hash)).ToString("D").ToUpperInvariant()
}

# Validate input.
$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path

if ([IO.Path]::GetExtension($ProjectPath) -ne ".fsproj") {
    throw "ProjectPath must point to an F# .fsproj file."
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must have three numeric parts, for example 1.0.0."
}

if ($ProductName -match '[\\/:*?"<>|]') {
    throw "ProductName contains characters that are not valid in a Windows file or directory name."
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path $ProjectPath -Parent) "artifacts"
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$safeName = $ProductName -replace '[^A-Za-z0-9_.-]', '_'
$projectDirectory = Split-Path $ProjectPath -Parent
$workDirectory = Join-Path ([IO.Path]::GetTempPath()) ("FSharpMsi-" + [Guid]::NewGuid().ToString("N"))
$publishDirectory = Join-Path $workDirectory "publish"
$wixDirectory = Join-Path $workDirectory "wix"

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $wixDirectory -Force | Out-Null

$msiPath = Join-Path $OutputDirectory "$safeName-$Version.msi"

# Keep the UpgradeCode stable between versions of the same product.
$upgradeCode = New-DeterministicGuid "$Manufacturer|$ProductName"

try {
    # Check that the .NET SDK is available.
    & dotnet --version
    if ($LASTEXITCODE -ne 0) {
        throw "The .NET SDK could not be started."
    }

    Write-Host ""
    Write-Host "Publishing $ProjectPath..." -ForegroundColor Cyan

    $publishArgs = @(
        "publish"
        $ProjectPath
        "--configuration", "Release"
        "--runtime", $RuntimeIdentifier
        "--output", $publishDirectory
        "--self-contained", $SelfContained.ToString().ToLowerInvariant()
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
    if ($publishedFiles.Count -eq 0) {
        throw "The publish directory contains no files."
    }

    # Escape values used in WiX XML.
    $xmlProductName = ConvertTo-XmlAttribute $ProductName
    $xmlManufacturer = ConvertTo-XmlAttribute $Manufacturer
    $xmlPublishDirectory = ConvertTo-XmlAttribute ($publishDirectory.TrimEnd('\') + '\**')
    $xmlDowngradeMessage = ConvertTo-XmlAttribute "A newer version of $ProductName is already installed."

    # Create the WiX project.
    $wixProject = @"
<Project Sdk="WixToolset.Sdk/$WixSdkVersion">
  <PropertyGroup>
    <OutputType>Package</OutputType>
    <InstallerPlatform>x64</InstallerPlatform>
    <OutputName>$safeName</OutputName>
  </PropertyGroup>
</Project>
"@

    $wixProjectPath = Join-Path $wixDirectory "AppPackage.wixproj"
    Set-Content -LiteralPath $wixProjectPath -Value $wixProject -Encoding UTF8

    # Create WiX authoring.
    $wixSource = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package
      Name="$xmlProductName"
      Manufacturer="$xmlManufacturer"
      Version="$Version"
      UpgradeCode="$upgradeCode"
      Scope="perMachine">

    <MajorUpgrade DowngradeErrorMessage="$xmlDowngradeMessage" />
    <MediaTemplate EmbedCab="yes" />

    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="$xmlProductName">
        <Files Include="$xmlPublishDirectory" />
      </Directory>
    </StandardDirectory>

  </Package>
</Wix>
"@

    $wixSourcePath = Join-Path $wixDirectory "Package.wxs"
    Set-Content -LiteralPath $wixSourcePath -Value $wixSource -Encoding UTF8

    Write-Host ""
    Write-Host "Building MSI using WiX Toolset SDK $WixSdkVersion..." -ForegroundColor Cyan

    & dotnet build $wixProjectPath --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "WiX build failed with exit code $LASTEXITCODE."
    }

    # Locate the MSI generated by WiX.
    $builtMsi = Get-ChildItem -LiteralPath $wixDirectory -Filter "$safeName.msi" -File -Recurse |
        Select-Object -First 1

    if (-not $builtMsi) {
        throw "WiX build completed, but the expected MSI '$safeName.msi' was not found."
    }

    Copy-Item -LiteralPath $builtMsi.FullName -Destination $msiPath -Force

    Write-Host ""
    Write-Host "MSI successfully created:" -ForegroundColor Green
    Write-Host $msiPath -ForegroundColor Green
    Write-Host ""
    Write-Host "UpgradeCode: $upgradeCode"
}
finally {
    if ($KeepBuildFiles) {
        Write-Host "Temporary build files retained at: $workDirectory"
    }
    elseif (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}
