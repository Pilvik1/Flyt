[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime,

    [Parameter(Mandatory)]
    [string] $PackagePath,

    [Parameter(Mandatory)]
    [string] $ChecksumPath,

    [Parameter(Mandatory)]
    [string] $SbomPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($path in @($PackagePath, $ChecksumPath, $SbomPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required package artifact not found at $path."
    }
}

$expectedChecksum = ((Get-Content -LiteralPath $ChecksumPath -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actualChecksum = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualChecksum -ne $expectedChecksum) {
    throw "Checksum mismatch for ${PackagePath}: expected $expectedChecksum, found $actualChecksum."
}

$externalSbom = Get-Content -LiteralPath $SbomPath -Raw | ConvertFrom-Json
if ([string]$externalSbom.spdxVersion -ne 'SPDX-2.2') {
    throw "External SBOM is not SPDX 2.2: $SbomPath."
}
if (@($externalSbom.packages).Count -eq 0) {
    throw "External SBOM contains no packages: $SbomPath."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
try {
    $entries = @($archive.Entries)
    $entryNames = @($entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $requiredEntries = @(
        'flyt.exe',
        'Flyt/tools/flyt.exe',
        'LICENSE',
        'THIRD-PARTY-NOTICES.md',
        'licenses/README.md',
        'licenses/MIT.txt',
        'licenses/Apache-2.0.txt',
        '_manifest/spdx_2.2/manifest.spdx.json'
    )
    foreach ($required in $requiredEntries) {
        if ($entryNames -cnotcontains $required) {
            throw "Required archive entry '$required' is missing from $PackagePath."
        }
    }

    $runtimePrefix = "licenses/microsoft.netcore.app.runtime.$Runtime-"
    if (-not ($entryNames | Where-Object { $_ -match "^$([regex]::Escape($runtimePrefix))LICENSE(\..*)?$" })) {
        throw "The archive is missing the resolved .NET runtime LICENSE for $Runtime."
    }
    if (-not ($entryNames | Where-Object { $_ -match "^$([regex]::Escape($runtimePrefix))THIRD-PARTY-NOTICES(\..*)?$" })) {
        throw "The archive is missing the resolved .NET runtime THIRD-PARTY-NOTICES for $Runtime."
    }

    foreach ($entry in $entries) {
        if ($entry.FullName.EndsWith('/')) {
            continue
        }
        $stream = $entry.Open()
        try {
            $stream.CopyTo([IO.Stream]::Null)
        }
        finally {
            $stream.Dispose()
        }
    }

    $embeddedSbomEntry = $entries | Where-Object {
        $_.FullName.Replace('\', '/') -eq '_manifest/spdx_2.2/manifest.spdx.json'
    } | Select-Object -First 1
    $reader = [IO.StreamReader]::new($embeddedSbomEntry.Open())
    try {
        $embeddedSbom = $reader.ReadToEnd() | ConvertFrom-Json
    }
    finally {
        $reader.Dispose()
    }
    if ([string]$embeddedSbom.spdxVersion -ne 'SPDX-2.2') {
        throw "Embedded SBOM is not SPDX 2.2 in $PackagePath."
    }
}
finally {
    $archive.Dispose()
}

[pscustomobject]@{
    Runtime = $Runtime
    Package = [IO.Path]::GetFullPath($PackagePath)
    Sha256 = $actualChecksum
    Entries = $entryNames.Count
    Packages = @($externalSbom.packages).Count
}
