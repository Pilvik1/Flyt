[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $PublishWindows
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'FlytV1.slnx'

dotnet restore $solution
dotnet build $solution --configuration $Configuration --no-restore
dotnet test $solution --configuration $Configuration --no-build

if ($PublishWindows) {
    $output = Join-Path $root 'artifacts/win-x64'
    dotnet publish (Join-Path $root 'src/Flyt.Cli/Flyt.Cli.csproj') `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        --output $output
}

