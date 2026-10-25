[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/packages')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Flyt.Cli/Flyt.Cli.csproj'
$moduleSource = Join-Path $root 'powershell/Flyt'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version is missing from $project."
}

$moduleManifest = Import-PowerShellDataFile (Join-Path $moduleSource 'Flyt.psd1')
if ([string]$moduleManifest.ModuleVersion -ne $version) {
    throw "CLI version $version and module version $($moduleManifest.ModuleVersion) do not match."
}

$artifactRoot = [IO.Path]::GetFullPath($OutputDirectory)
$publishDirectory = Join-Path $artifactRoot "publish-$Runtime"
$stagingDirectory = Join-Path $artifactRoot "staging-$Runtime"
$packageName = "Flyt-v$version-$Runtime.zip"
$packagePath = Join-Path $artifactRoot $packageName
$checksumPath = "$packagePath.sha256"
$sbomPath = Join-Path $artifactRoot "Flyt-v$version-$Runtime.spdx.json"

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
foreach ($generatedPath in @($publishDirectory, $stagingDirectory)) {
    if (Test-Path -LiteralPath $generatedPath) {
        Remove-Item -LiteralPath $generatedPath -Recurse -Force
    }
}

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for $Runtime with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
$executable = Join-Path $publishDirectory 'flyt.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Published executable not found at $executable."
}

Copy-Item -LiteralPath $executable -Destination (Join-Path $stagingDirectory 'flyt.exe')
Copy-Item -LiteralPath $moduleSource -Destination (Join-Path $stagingDirectory 'Flyt') -Recurse
$documentationDirectory = Join-Path $stagingDirectory 'docs'
New-Item -ItemType Directory -Path $documentationDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $stagingDirectory 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stagingDirectory 'LICENSE')
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $stagingDirectory 'THIRD-PARTY-NOTICES.md')
Copy-Item -LiteralPath (Join-Path $root 'docs/operator-guide.md') -Destination $documentationDirectory
Copy-Item -LiteralPath (Join-Path $root 'docs/desk-cockpit.md') -Destination $documentationDirectory
$moduleTools = Join-Path $stagingDirectory 'Flyt/tools'
New-Item -ItemType Directory -Path $moduleTools -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $moduleTools 'flyt.exe')

[ordered]@{
    product = 'Flyt'
    version = $version
    runtime = $Runtime
    createdAt = (Get-Date).ToUniversalTime().ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stagingDirectory 'install-manifest.json') -Encoding utf8

foreach ($generatedFile in @($packagePath, $checksumPath, $sbomPath)) {
    if (Test-Path -LiteralPath $generatedFile) {
        Remove-Item -LiteralPath $generatedFile -Force
    }
}

dotnet tool run sbom-tool -- generate `
    -b $stagingDirectory `
    -bc $root `
    -pn Flyt `
    -pv $version `
    -ps 'Håkon Pilvik' `
    -nsb 'https://github.com/Pilvik1/Flyt'
if ($LASTEXITCODE -ne 0) {
    throw "SBOM generation failed for $Runtime with exit code $LASTEXITCODE."
}

$generatedSbom = Join-Path $stagingDirectory '_manifest/spdx_2.2/manifest.spdx.json'
if (-not (Test-Path -LiteralPath $generatedSbom)) {
    throw "Generated SBOM not found at $generatedSbom."
}
Copy-Item -LiteralPath $generatedSbom -Destination $sbomPath

Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $packagePath -CompressionLevel Optimal
$checksum = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $packageName" | Set-Content -LiteralPath $checksumPath -Encoding ascii

[pscustomobject]@{
    Version = $version
    Runtime = $Runtime
    Package = $packagePath
    Checksum = $checksumPath
    Sbom = $sbomPath
}
