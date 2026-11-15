[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [string]$ProductName,

    [string]$Manufacturer = "Local Test",

    [string]$Version = "1.0.0",

    [string]$RuntimeIdentifier = "win-x64",

    [bool]$SelfContained = $true,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot "output")
)

$ErrorActionPreference = "Stop"

function Add-DirectoryContents {
    param(
        [System.Xml.XmlElement]$ParentElement,
        [string]$CurrentPath,
        [string]$PublishRoot,
        [System.Xml.XmlDocument]$Document,
        [string]$Namespace,
        [System.Collections.Generic.List[string]]$ComponentIds,
        [ref]$Counter
    )

    Get-ChildItem -LiteralPath $CurrentPath -Directory | Sort-Object Name | ForEach-Object {
        $Counter.Value++
        $directoryId = "dir{0:D5}" -f $Counter.Value
        $directoryElement = $Document.CreateElement("Directory", $Namespace)
        $directoryElement.SetAttribute("Id", $directoryId)
        $directoryElement.SetAttribute("Name", $_.Name)
        [void]$ParentElement.AppendChild($directoryElement)

        Add-DirectoryContents -ParentElement $directoryElement `
            -CurrentPath $_.FullName -PublishRoot $PublishRoot `
            -Document $Document -Namespace $Namespace `
            -ComponentIds $ComponentIds -Counter $Counter
    }

    Get-ChildItem -LiteralPath $CurrentPath -File | Sort-Object Name | ForEach-Object {
        $Counter.Value++
        $componentId = "cmp{0:D5}" -f $Counter.Value
        $fileId = "fil{0:D5}" -f $Counter.Value

        $component = $Document.CreateElement("Component", $Namespace)
        $component.SetAttribute("Id", $componentId)
        $component.SetAttribute("Guid", "*")

        $file = $Document.CreateElement("File", $Namespace)
        $file.SetAttribute("Id", $fileId)
        $file.SetAttribute("Source", $_.FullName)
        $file.SetAttribute("KeyPath", "yes")

        [void]$component.AppendChild($file)
        [void]$ParentElement.AppendChild($component)
        $ComponentIds.Add($componentId)
    }
}

# Resolve and validate inputs.
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
    throw ".NET SDK was not found. Install the .NET SDK and ensure 'dotnet' is on PATH."
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
        --self-contained $selfContainedText --output $publishDirectory `
        -p:UseAppHost=true

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
    if ($publishedFiles.Count -eq 0) {
        throw "The publish directory is empty; no MSI can be created."
    }

    # WiX v4+ project and source.
    $wixProject = Join-Path $wixDirectory "HelloWorld.wixproj"
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
    # Keep this UpgradeCode unchanged for future versions of this product.
    $package.SetAttribute("UpgradeCode", "6D0B8A2E-3B31-4C56-9B29-6D7A7F1A0C41")
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
    $componentGroupRef = $document.CreateElement("ComponentGroupRef", $namespace)
    $componentGroupRef.SetAttribute("Id", "AppComponents")
    [void]$feature.AppendChild($componentGroupRef)
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
    Add-DirectoryContents -ParentElement $installDirectory `
        -CurrentPath $publishDirectory -PublishRoot $publishDirectory `
        -Document $document -Namespace $namespace `
        -ComponentIds $componentIds -Counter ([ref]$counter)

    $componentGroup = $document.CreateElement("ComponentGroup", $namespace)
    $componentGroup.SetAttribute("Id", "AppComponents")
    [void]$fragment.AppendChild($componentGroup)

    foreach ($id in $componentIds) {
        $componentRef = $document.CreateElement("ComponentRef", $namespace)
        $componentRef.SetAttribute("Id", $id)
        [void]$componentGroup.AppendChild($componentRef)
    }

    $wxsPath = Join-Path $wixDirectory "Package.wxs"
    $document.Save($wxsPath)

    Write-Host "Building MSI with WiX Toolset 7.0.1..." -ForegroundColor Cyan
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
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $msi) {
        throw "WiX build completed but no MSI file was found."
    }

    $safeName = ($ProductName -replace '[\\/:*?"<>|]', '_')
    $destination = Join-Path $OutputDirectory ($safeName + "-" + $Version + ".msi")
    Copy-Item -LiteralPath $msi.FullName -Destination $destination -Force

    Write-Host ""
    Write-Host "MSI created: $destination" -ForegroundColor Green
    Write-Host "Product name: $ProductName"
    Write-Host "Version:      $Version"
    Write-Host "Architecture: x64"
    Write-Host "Runtime:      $RuntimeIdentifier (SelfContained=$SelfContained)"
}
finally {
    if (Test-Path -LiteralPath $buildRoot) {
        Remove-Item -LiteralPath $buildRoot -Recurse -Force
    }
}
