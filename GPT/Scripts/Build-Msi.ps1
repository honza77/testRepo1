[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [string]$ProductName,
    [string]$Manufacturer = "Local Test",
    [string]$Version = "1.0.0",
    [string]$RuntimeIdentifier = "win-x64",
    [bool]$SelfContained = $true,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\output")
)

$ErrorActionPreference = "Stop"

function New-DeterministicGuid {
    param([Parameter(Mandatory = $true)][string]$Text)

    # UUID v5-style deterministic GUID, so the same product identity keeps
    # the same UpgradeCode across builds.
    $namespace = [Guid]"6ba7b810-9dad-11d1-80b4-00c04fd430c8"
    $namespaceBytes = $namespace.ToByteArray()
    [Array]::Reverse($namespaceBytes, 0, 4)
    [Array]::Reverse($namespaceBytes, 4, 2)
    [Array]::Reverse($namespaceBytes, 6, 2)

    $nameBytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $inputBytes = New-Object byte[] ($namespaceBytes.Length + $nameBytes.Length)
    [Array]::Copy($namespaceBytes, 0, $inputBytes, 0, $namespaceBytes.Length)
    [Array]::Copy($nameBytes, 0, $inputBytes, $namespaceBytes.Length, $nameBytes.Length)

    $sha1 = [Security.Cryptography.SHA1]::Create()
    try { $hash = $sha1.ComputeHash($inputBytes) }
    finally { $sha1.Dispose() }

    $guidBytes = New-Object byte[] 16
    [Array]::Copy($hash, $guidBytes, 16)
    $guidBytes[6] = ($guidBytes[6] -band 0x0F) -bor 0x50
    $guidBytes[8] = ($guidBytes[8] -band 0x3F) -bor 0x80
    [Array]::Reverse($guidBytes, 0, 4)
    [Array]::Reverse($guidBytes, 4, 2)
    [Array]::Reverse($guidBytes, 6, 2)
    return ([Guid]::new($guidBytes)).ToString().ToUpperInvariant()
}

function Add-PublishedFiles {
    param(
        [System.Xml.XmlElement]$DirectoryElement,
        [string]$Path,
        [System.Xml.XmlDocument]$Document,
        [string]$Namespace,
        [System.Collections.Generic.List[string]]$ComponentIds,
        [ref]$Counter
    )

    foreach ($directory in (Get-ChildItem -LiteralPath $Path -Directory | Sort-Object Name)) {
        $Counter.Value++
        $dir = $Document.CreateElement("Directory", $Namespace)
        $dir.SetAttribute("Id", ("dir{0:D5}" -f $Counter.Value))
        $dir.SetAttribute("Name", $directory.Name)
        [void]$DirectoryElement.AppendChild($dir)
        Add-PublishedFiles -DirectoryElement $dir -Path $directory.FullName `
            -Document $Document -Namespace $Namespace `
            -ComponentIds $ComponentIds -Counter $Counter
    }

    foreach ($fileInfo in (Get-ChildItem -LiteralPath $Path -File | Sort-Object Name)) {
        $Counter.Value++
        $componentId = "cmp{0:D5}" -f $Counter.Value
        $fileId = "fil{0:D5}" -f $Counter.Value

        $component = $Document.CreateElement("Component", $Namespace)
        $component.SetAttribute("Id", $componentId)
        $component.SetAttribute("Guid", "*")

        $file = $Document.CreateElement("File", $Namespace)
        $file.SetAttribute("Id", $fileId)
        $file.SetAttribute("Source", $fileInfo.FullName)
        $file.SetAttribute("KeyPath", "yes")

        [void]$component.AppendChild($file)
        [void]$DirectoryElement.AppendChild($component)
        $ComponentIds.Add($componentId)
    }
}

$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
if ([IO.Path]::GetExtension($ProjectPath) -ne ".fsproj") {
    throw "ProjectPath must point to an F# .fsproj file."
}
if (-not $ProductName) {
    $ProductName = [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Version must have three or four numeric parts, e.g. 1.0.0."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found. Install the .NET SDK and ensure dotnet is on PATH."
}
if ($RuntimeIdentifier -ne "win-x64") {
    throw "This MSI template is configured for x64 Windows. Use -RuntimeIdentifier win-x64."
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$buildRoot = Join-Path ([IO.Path]::GetTempPath()) ("FSharpMsi-" + [guid]::NewGuid().ToString("N"))
$publishDirectory = Join-Path $buildRoot "publish"
$wixDirectory = Join-Path $buildRoot "wix"
New-Item -ItemType Directory -Path $publishDirectory, $wixDirectory -Force | Out-Null

try {
    Write-Host "Publishing $ProjectPath..." -ForegroundColor Cyan
    $selfContainedText = $SelfContained.ToString().ToLowerInvariant()
    & dotnet publish $ProjectPath --configuration Release --runtime $RuntimeIdentifier `
        --self-contained $selfContainedText --output $publishDirectory -p:UseAppHost=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    if (@(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse).Count -eq 0) {
        throw "The publish directory is empty; no MSI can be created."
    }

    $wixProject = Join-Path $wixDirectory "AppPackage.wixproj"
    @'
<Project Sdk="WixToolset.Sdk/7.0.1">
</Project>
'@ | Set-Content -LiteralPath $wixProject -Encoding UTF8

    $namespace = "http://wixtoolset.org/schemas/v4/wxs"
    $document = New-Object System.Xml.XmlDocument
    $declaration = $document.CreateXmlDeclaration("1.0", "utf-8", $null)
    [void]$document.AppendChild($declaration)
    $wix = $document.CreateElement("Wix", $namespace)
    [void]$document.AppendChild($wix)

    $package = $document.CreateElement("Package", $namespace)
    $package.SetAttribute("Name", $ProductName)
    $package.SetAttribute("Manufacturer", $Manufacturer)
    $package.SetAttribute("Version", $Version)
    $upgradeCode = New-DeterministicGuid -Text ($Manufacturer + "|" + $ProductName)
    $package.SetAttribute("UpgradeCode", $upgradeCode)
    $package.SetAttribute("Scope", "perMachine")
    $package.SetAttribute("Platform", "x64")
    [void]$wix.AppendChild($package)

    $majorUpgrade = $document.CreateElement("MajorUpgrade", $namespace)
    $majorUpgrade.SetAttribute("DowngradeErrorMessage", "A newer version of $ProductName is already installed.")
    [void]$package.AppendChild($majorUpgrade)

    $mediaTemplate = $document.CreateElement("MediaTemplate", $namespace)
    [void]$package.AppendChild($mediaTemplate)

    $feature = $document.CreateElement("Feature", $namespace)
    $feature.SetAttribute("Id", "MainFeature")
    $feature.SetAttribute("Title", $ProductName)
    $feature.SetAttribute("Level", "1")
    $groupRef = $document.CreateElement("ComponentGroupRef", $namespace)
    $groupRef.SetAttribute("Id", "AppComponents")
    [void]$feature.AppendChild($groupRef)
    [void]$package.AppendChild($feature)

    $fragment = $document.CreateElement("Fragment", $namespace)
    [void]$wix.AppendChild($fragment)

    $programFiles = $document.CreateElement("StandardDirectory", $namespace)
    $programFiles.SetAttribute("Id", "ProgramFiles64Folder")
    [void]$fragment.AppendChild($programFiles)

    $installDirectory = $document.CreateElement("Directory", $namespace)
    $installDirectory.SetAttribute("Id", "INSTALLFOLDER")
    $installDirectory.SetAttribute("Name", $ProductName)
    [void]$programFiles.AppendChild($installDirectory)

    $componentIds = New-Object 'System.Collections.Generic.List[string]'
    $counter = 0
    Add-PublishedFiles -DirectoryElement $installDirectory -Path $publishDirectory `
        -Document $document -Namespace $namespace `
        -ComponentIds $componentIds -Counter ([ref]$counter)

    $componentGroup = $document.CreateElement("ComponentGroup", $namespace)
    $componentGroup.SetAttribute("Id", "AppComponents")
    [void]$fragment.AppendChild($componentGroup)
    foreach ($componentId in $componentIds) {
        $componentRef = $document.CreateElement("ComponentRef", $namespace)
        $componentRef.SetAttribute("Id", $componentId)
        [void]$componentGroup.AppendChild($componentRef)
    }

    $document.Save((Join-Path $wixDirectory "Package.wxs"))

    Write-Host "Building MSI using WiX Toolset SDK 7.0.1..." -ForegroundColor Cyan
    Push-Location $wixDirectory
    try {
        & dotnet build $wixProject --configuration Release
        if ($LASTEXITCODE -ne 0) {
            throw "WiX build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    $msi = Get-ChildItem -LiteralPath $wixDirectory -Filter "*.msi" -File -Recurse |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $msi) {
        throw "Build completed but no MSI file was found."
    }

    $safeName = $ProductName -replace '[\\/:*?"<>|]', '_'
    $destination = Join-Path $OutputDirectory ($safeName + "-" + $Version + ".msi")
    Copy-Item -LiteralPath $msi.FullName -Destination $destination -Force

    Write-Host "`nMSI created: $destination" -ForegroundColor Green
    Write-Host "UpgradeCode: $upgradeCode"
    Write-Host "Runtime: $RuntimeIdentifier; SelfContained: $SelfContained"
}
finally {
    if (Test-Path -LiteralPath $buildRoot) {
        Remove-Item -LiteralPath $buildRoot -Recurse -Force
    }
}
