[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime,

    [Parameter(Mandatory)]
    [string] $AssetsFile,

    [Parameter(Mandatory)]
    [string] $DestinationDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$licenseSource = Join-Path $root 'licenses'
if (-not (Test-Path -LiteralPath $licenseSource -PathType Container)) {
    throw "Tracked license directory not found at $licenseSource."
}
if (-not (Test-Path -LiteralPath $AssetsFile -PathType Leaf)) {
    throw "NuGet assets file not found at $AssetsFile."
}

$destination = [IO.Path]::GetFullPath($DestinationDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path (Join-Path $licenseSource '*') -Destination $destination -Recurse -Force

$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
$packageRoots = @(
    $assets.packageFolders.Keys |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container }
)
if ($packageRoots.Count -eq 0) {
    throw 'No restored NuGet package root from project.assets.json exists on disk.'
}
$libraryKeys = @($assets.libraries.Keys)

$expectedLicenses = [ordered]@{
    'Microsoft.Data.Sqlite' = 'MIT'
    'Microsoft.Data.Sqlite.Core' = 'MIT'
    'SQLitePCLRaw.bundle_e_sqlite3' = 'Apache-2.0'
    'SQLitePCLRaw.core' = 'Apache-2.0'
    'SQLitePCLRaw.lib.e_sqlite3' = 'Apache-2.0'
    'SQLitePCLRaw.provider.e_sqlite3' = 'Apache-2.0'
    'Spectre.Console' = 'MIT'
    'Spectre.Console.Ansi' = 'MIT'
    'Microsoft.NET.ILLink.Tasks' = 'MIT'
    "Microsoft.NETCore.App.Runtime.$Runtime" = 'MIT'
    "Microsoft.AspNetCore.App.Runtime.$Runtime" = 'MIT'
    "Microsoft.WindowsDesktop.App.Runtime.$Runtime" = 'MIT'
}

$requiredPackages = @(
    'Microsoft.Data.Sqlite',
    'Microsoft.Data.Sqlite.Core',
    'SQLitePCLRaw.bundle_e_sqlite3',
    'SQLitePCLRaw.core',
    'SQLitePCLRaw.lib.e_sqlite3',
    'SQLitePCLRaw.provider.e_sqlite3',
    'Spectre.Console',
    'Spectre.Console.Ansi'
)

$resolved = [ordered]@{}
foreach ($entry in $expectedLicenses.GetEnumerator()) {
    $packageId = [string]$entry.Key
    $matches = @(
        $libraryKeys | Where-Object {
            $_.StartsWith("$packageId/", [StringComparison]::OrdinalIgnoreCase)
        }
    )
    if ($matches.Count -eq 0) {
        if ($requiredPackages -contains $packageId) {
            throw "Required package $packageId is absent from $AssetsFile."
        }
        continue
    }
    if ($matches.Count -ne 1) {
        throw "Expected one resolved version of $packageId, found $($matches.Count)."
    }

    $parts = $matches[0] -split '/', 2
    $version = $parts[1]
    $packageDirectory = @(
        $packageRoots | ForEach-Object {
            Join-Path (Join-Path $_ $packageId.ToLowerInvariant()) $version
        } | Where-Object { Test-Path -LiteralPath $_ -PathType Container }
    ) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($packageDirectory)) {
        throw "Restored package directory for $packageId $version was not found."
    }
    $nuspecPath = Join-Path $packageDirectory "$($packageId.ToLowerInvariant()).nuspec"
    if (-not (Test-Path -LiteralPath $nuspecPath -PathType Leaf)) {
        throw "NuGet metadata for $packageId $version not found at $nuspecPath."
    }

    [xml]$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
    $licenseNode = $nuspec.package.metadata.license
    $actualLicense = if ($null -eq $licenseNode) { '' } else { [string]$licenseNode.InnerText }
    if (-not [string]::Equals($actualLicense.Trim(), [string]$entry.Value, [StringComparison]::Ordinal)) {
        throw "License mismatch for $packageId ${version}: expected $($entry.Value), found '$actualLicense'."
    }

    $resolved[$packageId] = [pscustomobject]@{
        Version = $version
        Directory = $packageDirectory
        License = $actualLicense.Trim()
    }
}

$runtimeLegalFiles = 0
$runtimePackagePrefixes = @(
    'Microsoft.NETCore.App.Runtime.',
    'Microsoft.AspNetCore.App.Runtime.',
    'Microsoft.WindowsDesktop.App.Runtime.'
)
foreach ($entry in $resolved.GetEnumerator()) {
    $isRuntimePackage = $entry.Key -eq 'Microsoft.NET.ILLink.Tasks'
    foreach ($prefix in $runtimePackagePrefixes) {
        $isRuntimePackage = $isRuntimePackage -or $entry.Key.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase)
    }
    if (-not $isRuntimePackage) {
        continue
    }

    $safePackageName = $entry.Key.ToLowerInvariant()
    $legalFiles = @(
        Get-ChildItem -LiteralPath $entry.Value.Directory -File |
            Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\..*)?$' }
    )
    foreach ($file in $legalFiles) {
        Copy-Item -LiteralPath $file.FullName `
            -Destination (Join-Path $destination "$safePackageName-$($file.Name)") `
            -Force
        $runtimeLegalFiles++
    }
}

$netCorePackage = "Microsoft.NETCore.App.Runtime.$Runtime"
$netCorePrefix = $netCorePackage.ToLowerInvariant()
$netCoreFiles = @(Get-ChildItem -LiteralPath $destination -File -Filter "$netCorePrefix-*")
$netCoreFileNames = @($netCoreFiles | ForEach-Object { $_.Name })
$hasNetCoreLicense = [bool]($netCoreFileNames -match '-LICENSE(\..*)?$')
$hasNetCoreNotices = [bool]($netCoreFileNames -match '-THIRD-PARTY-NOTICES(\..*)?$')
if (-not $hasNetCoreLicense -or -not $hasNetCoreNotices) {
    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $dotnetRootCandidates = @(
        $env:DOTNET_ROOT,
        (Split-Path -Parent $dotnetCommand.Source)
    ) | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        (Test-Path -LiteralPath $_ -PathType Container)
    } | Select-Object -Unique
    $dotnetRoot = $dotnetRootCandidates | Where-Object {
        @(Get-ChildItem -LiteralPath $_ -File | Where-Object {
            $_.Name -match '^LICENSE(\..*)?$'
        }).Count -gt 0 -and
        @(Get-ChildItem -LiteralPath $_ -File | Where-Object {
            $_.Name -match '^THIRD[-_]?PARTY[-_]?NOTICES(\..*)?$'
        }).Count -gt 0
    } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($dotnetRoot)) {
        throw 'The installed .NET distribution did not supply LICENSE and THIRD-PARTY-NOTICES files.'
    }

    $dotnetLicense = Get-ChildItem -LiteralPath $dotnetRoot -File | Where-Object {
        $_.Name -match '^LICENSE(\..*)?$'
    } | Select-Object -First 1
    $dotnetNotices = Get-ChildItem -LiteralPath $dotnetRoot -File | Where-Object {
        $_.Name -match '^THIRD[-_]?PARTY[-_]?NOTICES(\..*)?$'
    } | Select-Object -First 1
    Copy-Item -LiteralPath $dotnetLicense.FullName `
        -Destination (Join-Path $destination "$netCorePrefix-LICENSE.txt") `
        -Force
    Copy-Item -LiteralPath $dotnetNotices.FullName `
        -Destination (Join-Path $destination "$netCorePrefix-THIRD-PARTY-NOTICES.txt") `
        -Force
    $runtimeLegalFiles += 2
    $netCoreFiles = @(Get-ChildItem -LiteralPath $destination -File -Filter "$netCorePrefix-*")
    $netCoreFileNames = @($netCoreFiles | ForEach-Object { $_.Name })
}
if (-not ($netCoreFileNames -match '-LICENSE(\..*)?$')) {
    throw "The installed .NET distribution did not supply a LICENSE file for $netCorePackage."
}
if (-not ($netCoreFileNames -match '-THIRD-PARTY-NOTICES(\..*)?$')) {
    throw "The installed .NET distribution did not supply a THIRD-PARTY-NOTICES file for $netCorePackage."
}

[pscustomobject]@{
    Runtime = $Runtime
    VerifiedPackages = $resolved.Count
    RuntimeLegalFiles = $runtimeLegalFiles
    Destination = $destination
}
