param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputFile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Xml.Linq

$publishRoot = (Resolve-Path $PublishDir).Path.TrimEnd('\')
$outputPath = [System.IO.Path]::GetFullPath($OutputFile)
$outputDir = Split-Path -Parent $outputPath
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

$ns = [System.Xml.Linq.XNamespace]'http://wixtoolset.org/schemas/v4/wxs'
$wix = [System.Xml.Linq.XElement]::new($ns + 'Wix')

$directoryFragment = [System.Xml.Linq.XElement]::new($ns + 'Fragment')
$directoryRef = [System.Xml.Linq.XElement]::new($ns + 'DirectoryRef')
$directoryRef.SetAttributeValue('Id', 'INSTALLFOLDER')
$directoryFragment.Add($directoryRef)
$wix.Add($directoryFragment)

$componentGroupFragment = [System.Xml.Linq.XElement]::new($ns + 'Fragment')
$componentGroup = [System.Xml.Linq.XElement]::new($ns + 'ComponentGroup')
$componentGroup.SetAttributeValue('Id', 'PublishedFiles')
$componentGroupFragment.Add($componentGroup)
$wix.Add($componentGroupFragment)

$files = Get-ChildItem -Path $publishRoot -File -Recurse | Sort-Object FullName
if ($files.Count -eq 0) {
    throw "No published files were found under $publishRoot"
}

$filesByDirectory = @{}
$childDirectories = @{}
$directoryIds = @{}

function Get-RelativePath([string]$path) {
    if ($path.Length -le $publishRoot.Length) {
        return ''
    }

    return $path.Substring($publishRoot.Length).TrimStart('\')
}

function Get-RelativeDirectory([string]$path) {
    $relativeFile = Get-RelativePath $path
    $relativeDir = Split-Path -Parent $relativeFile
    if ($relativeDir -eq '.' -or [string]::IsNullOrWhiteSpace($relativeDir)) {
        return ''
    }

    return $relativeDir
}

function New-StableId([string]$prefix, [string]$value) {
    $safe = ($value -replace '[^A-Za-z0-9]', '_').Trim('_')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        $safe = 'Root'
    }

    if ($safe.Length -gt 40) {
        $safe = $safe.Substring(0, 40)
    }

    $sha1 = [System.Security.Cryptography.SHA1Managed]::Create()
    $hashBytes = $sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($value))
    $hash = ([System.BitConverter]::ToString($hashBytes)).Replace('-', '').Substring(0, 10)
    return "$prefix${safe}_$hash"
}

foreach ($file in $files) {
    $relativeDir = Get-RelativeDirectory $file.FullName
    if (-not $filesByDirectory.ContainsKey($relativeDir)) {
        $filesByDirectory[$relativeDir] = [System.Collections.Generic.List[object]]::new()
    }

    $filesByDirectory[$relativeDir].Add($file)
}

$directories = Get-ChildItem -Path $publishRoot -Directory -Recurse | Sort-Object FullName
foreach ($directory in $directories) {
    $relativeDir = Get-RelativePath $directory.FullName
    $directoryIds[$relativeDir] = New-StableId 'DIR_' $relativeDir

    $parentRelativeDir = Get-RelativeDirectory $directory.FullName
    if (-not $childDirectories.ContainsKey($parentRelativeDir)) {
        $childDirectories[$parentRelativeDir] = [System.Collections.Generic.List[string]]::new()
    }

    $childDirectories[$parentRelativeDir].Add($relativeDir)
}

function Add-DirectoryContent {
    param(
        [System.Xml.Linq.XElement]$ParentElement,
        [string]$RelativeDirectory
    )

    if ($filesByDirectory.ContainsKey($RelativeDirectory)) {
        foreach ($file in $filesByDirectory[$RelativeDirectory]) {
            $relativeFile = Get-RelativePath $file.FullName
            $componentId = New-StableId 'CMP_' $relativeFile
            $fileId = New-StableId 'FIL_' $relativeFile

            $component = [System.Xml.Linq.XElement]::new($ns + 'Component')
            $component.SetAttributeValue('Id', $componentId)
            $component.SetAttributeValue('Guid', '*')
            $component.SetAttributeValue('Bitness', 'always64')

            $fileElement = [System.Xml.Linq.XElement]::new($ns + 'File')
            $fileElement.SetAttributeValue('Id', $fileId)
            $fileElement.SetAttributeValue('Source', $file.FullName)
            $fileElement.SetAttributeValue('KeyPath', 'yes')

            $component.Add($fileElement)
            $ParentElement.Add($component)

            $componentRef = [System.Xml.Linq.XElement]::new($ns + 'ComponentRef')
            $componentRef.SetAttributeValue('Id', $componentId)
            $componentGroup.Add($componentRef)
        }
    }

    if ($childDirectories.ContainsKey($RelativeDirectory)) {
        foreach ($childRelativeDir in $childDirectories[$RelativeDirectory] | Sort-Object) {
            $directoryName = Split-Path -Leaf $childRelativeDir
            $directoryElement = [System.Xml.Linq.XElement]::new($ns + 'Directory')
            $directoryElement.SetAttributeValue('Id', $directoryIds[$childRelativeDir])
            $directoryElement.SetAttributeValue('Name', $directoryName)

            $ParentElement.Add($directoryElement)
            Add-DirectoryContent -ParentElement $directoryElement -RelativeDirectory $childRelativeDir
        }
    }
}

Add-DirectoryContent -ParentElement $directoryRef -RelativeDirectory ''

$document = [System.Xml.Linq.XDocument]::new([System.Xml.Linq.XDeclaration]::new('1.0', 'utf-8', 'yes'), $wix)
$document.Save($outputPath)
