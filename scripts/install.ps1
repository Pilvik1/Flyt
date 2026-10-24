[CmdletBinding()]
param(
    [string] $Version = 'latest',
    [ValidateSet('auto', 'x64', 'arm64')]
    [string] $Architecture = 'auto',
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs/Flyt'),
    [string] $ModuleRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PowerShell/Modules/Flyt'),
    [string] $PackagePath,
    [string] $ChecksumPath,
    [string] $GitHubToken = $env:FLYT_GITHUB_TOKEN,
    [switch] $InstallPsmux,
    [switch] $SkipPathUpdate,
    [switch] $NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repository = 'Pilvik1/FlytV1'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "flyt-install-$([guid]::NewGuid().ToString('N'))"
$currentDirectory = Join-Path $InstallRoot 'current'
$backupDirectory = Join-Path $InstallRoot ".backup-$([guid]::NewGuid().ToString('N'))"
$stagedDirectory = Join-Path $InstallRoot ".staged-$([guid]::NewGuid().ToString('N'))"
$installedModuleDirectory = $null
$moduleBackupDirectory = $null
$appSwapped = $false
$moduleSwapped = $false
$moduleInstallStarted = $false

function Get-FlytArchitecture {
    if ($Architecture -ne 'auto') {
        return $Architecture
    }

    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) {
        return 'arm64'
    }

    return 'x64'
}

function Get-FlytGitHubHeaders {
    $token = $GitHubToken
    if ([string]::IsNullOrWhiteSpace($token)) {
        foreach ($environmentName in @('GH_TOKEN', 'GITHUB_TOKEN')) {
            $candidate = [Environment]::GetEnvironmentVariable($environmentName)
            if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                $token = $candidate
                break
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($token) -and (Get-Command gh -ErrorAction SilentlyContinue)) {
        $token = (& gh auth token 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) {
            $token = $null
        }
    }

    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'FlytV1 is private. Authenticate GitHub CLI (`gh auth login`) or set FLYT_GITHUB_TOKEN before installing.'
    }

    return @{
        Accept = 'application/vnd.github+json'
        Authorization = "Bearer $token"
        'X-GitHub-Api-Version' = '2022-11-28'
    }
}

function Save-FlytReleaseAsset {
    param(
        [Parameter(Mandatory)] $Asset,
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [hashtable] $Headers
    )

    $downloadHeaders = @{} + $Headers
    $downloadHeaders.Accept = 'application/octet-stream'
    Invoke-WebRequest -Uri $Asset.url -Headers $downloadHeaders -OutFile $Destination
}

function Add-FlytToUserPath {
    param([Parameter(Mandatory)][string] $Path)

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($entries -notcontains $Path) {
        $newPath = (@($entries) + $Path) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    }

    if (($env:Path -split ';') -notcontains $Path) {
        $env:Path = "$Path;$env:Path"
    }
}

if (-not $IsWindows) {
    throw 'FlytV1 currently supports native Windows only.'
}

$resolvedArchitecture = Get-FlytArchitecture
$runtime = "win-$resolvedArchitecture"
New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null

try {
    $localPackage = -not [string]::IsNullOrWhiteSpace($PackagePath)
    if ($localPackage) {
        if ([string]::IsNullOrWhiteSpace($ChecksumPath)) {
            $ChecksumPath = "$PackagePath.sha256"
        }

        $downloadedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
        $downloadedChecksum = (Resolve-Path -LiteralPath $ChecksumPath).Path
    }
    else {
        $headers = Get-FlytGitHubHeaders
        $releaseUri = if ($Version -eq 'latest') {
            "https://api.github.com/repos/$repository/releases/latest"
        }
        else {
            "https://api.github.com/repos/$repository/releases/tags/v$($Version.TrimStart('v'))"
        }

        $release = Invoke-RestMethod -Uri $releaseUri -Headers $headers
        $releaseVersion = [string]$release.tag_name
        $packageName = "FlytV1-$releaseVersion-$runtime.zip"
        $checksumName = "$packageName.sha256"
        $packageAsset = @($release.assets | Where-Object name -eq $packageName)[0]
        $checksumAsset = @($release.assets | Where-Object name -eq $checksumName)[0]
        if (-not $packageAsset -or -not $checksumAsset) {
            throw "Release $releaseVersion does not contain $packageName and its SHA-256 file."
        }

        $downloadedPackage = Join-Path $temporaryDirectory $packageName
        $downloadedChecksum = Join-Path $temporaryDirectory $checksumName
        Save-FlytReleaseAsset -Asset $packageAsset -Destination $downloadedPackage -Headers $headers
        Save-FlytReleaseAsset -Asset $checksumAsset -Destination $downloadedChecksum -Headers $headers
    }

    $expectedChecksum = ((Get-Content -LiteralPath $downloadedChecksum -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
    if ($expectedChecksum -notmatch '^[a-f0-9]{64}$') {
        throw "Invalid SHA-256 file: $downloadedChecksum"
    }

    $actualChecksum = (Get-FileHash -LiteralPath $downloadedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualChecksum -ne $expectedChecksum) {
        throw "Package checksum mismatch. Expected $expectedChecksum but received $actualChecksum."
    }

    $extractedDirectory = Join-Path $temporaryDirectory 'package'
    Expand-Archive -LiteralPath $downloadedPackage -DestinationPath $extractedDirectory -Force
    $manifestPath = Join-Path $extractedDirectory 'install-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.product -ne 'FlytV1' -or $manifest.runtime -ne $runtime) {
        throw "Package manifest does not match FlytV1/$runtime."
    }

    $executable = Join-Path $extractedDirectory 'flyt.exe'
    $module = Join-Path $extractedDirectory 'Flyt'
    if (-not (Test-Path -LiteralPath $executable) -or -not (Test-Path -LiteralPath (Join-Path $module 'Flyt.psd1'))) {
        throw 'Package is missing flyt.exe or the Flyt PowerShell module.'
    }

    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    Copy-Item -LiteralPath $extractedDirectory -Destination $stagedDirectory -Recurse
    if (Test-Path -LiteralPath $currentDirectory) {
        Move-Item -LiteralPath $currentDirectory -Destination $backupDirectory
    }
    Move-Item -LiteralPath $stagedDirectory -Destination $currentDirectory
    $appSwapped = $true

    $installedModuleDirectory = Join-Path $ModuleRoot ([string]$manifest.version)
    $moduleBackupDirectory = "$installedModuleDirectory.backup-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $ModuleRoot -Force | Out-Null
    if (Test-Path -LiteralPath $installedModuleDirectory) {
        Move-Item -LiteralPath $installedModuleDirectory -Destination $moduleBackupDirectory
    }
    $moduleInstallStarted = $true
    Copy-Item -LiteralPath (Join-Path $currentDirectory 'Flyt') -Destination $installedModuleDirectory -Recurse
    $moduleSwapped = $true

    if (-not $SkipPathUpdate) {
        Add-FlytToUserPath -Path $currentDirectory
    }

    if ($InstallPsmux -and -not (Get-Command psmux -ErrorAction SilentlyContinue)) {
        $winget = Get-Command winget -ErrorAction SilentlyContinue
        if (-not $winget) {
            throw 'winget is required for -InstallPsmux. Install psmux manually and re-run `flyt doctor`.'
        }

        & $winget.Source install psmux --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) {
            throw "winget could not install psmux (exit code $LASTEXITCODE)."
        }
    }

    & (Join-Path $currentDirectory 'flyt.exe') version
    if ($LASTEXITCODE -ne 0) {
        throw "Installed Flyt executable failed its version check (exit code $LASTEXITCODE)."
    }
    Import-Module (Join-Path $installedModuleDirectory 'Flyt.psd1') -Force
    Get-Command Start-FlytDesk -ErrorAction Stop | Out-Null

    if (Test-Path -LiteralPath $backupDirectory) {
        Remove-Item -LiteralPath $backupDirectory -Recurse -Force
    }
    if ($moduleBackupDirectory -and (Test-Path -LiteralPath $moduleBackupDirectory)) {
        Remove-Item -LiteralPath $moduleBackupDirectory -Recurse -Force
    }

    Write-Host "FlytV1 $($manifest.version) installed successfully."
    Write-Host 'Run: flyt doctor'
    if (-not (Get-Command psmux -ErrorAction SilentlyContinue)) {
        Write-Warning 'psmux was not found. Re-run with -InstallPsmux or install it before starting a Desk.'
    }
}
catch {
    if ($moduleInstallStarted -and $installedModuleDirectory -and (Test-Path -LiteralPath $installedModuleDirectory)) {
        Remove-Item -LiteralPath $installedModuleDirectory -Recurse -Force
    }
    if ($moduleBackupDirectory -and (Test-Path -LiteralPath $moduleBackupDirectory)) {
        Move-Item -LiteralPath $moduleBackupDirectory -Destination $installedModuleDirectory
    }
    if ($appSwapped -and (Test-Path -LiteralPath $currentDirectory)) {
        Remove-Item -LiteralPath $currentDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $backupDirectory) {
        Move-Item -LiteralPath $backupDirectory -Destination $currentDirectory
    }

    throw
}
finally {
    if (Test-Path -LiteralPath $stagedDirectory) {
        Remove-Item -LiteralPath $stagedDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
