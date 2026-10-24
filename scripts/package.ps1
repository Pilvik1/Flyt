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
$packageName = "FlytV1-v$version-$Runtime.zip"
$packagePath = Join-Path $artifactRoot $packageName
$checksumPath = "$packagePath.sha256"

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
$moduleTools = Join-Path $stagingDirectory 'Flyt/tools'
New-Item -ItemType Directory -Path $moduleTools -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $moduleTools 'flyt.exe')

[ordered]@{
    product = 'FlytV1'
    version = $version
    runtime = $Runtime
    createdAt = (Get-Date).ToUniversalTime().ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stagingDirectory 'install-manifest.json') -Encoding utf8

foreach ($generatedFile in @($packagePath, $checksumPath)) {
    if (Test-Path -LiteralPath $generatedFile) {
        Remove-Item -LiteralPath $generatedFile -Force
    }
}

Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $packagePath -CompressionLevel Optimal
$checksum = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $packageName" | Set-Content -LiteralPath $checksumPath -Encoding ascii

[pscustomobject]@{
    Version = $version
    Runtime = $Runtime
    Package = $packagePath
    Checksum = $checksumPath
}
