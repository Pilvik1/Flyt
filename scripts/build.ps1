[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $PublishWindows
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'Flyt.slnx'

dotnet restore $solution
dotnet build $solution --configuration $Configuration --no-restore
dotnet test $solution --configuration $Configuration --no-build

if ($PublishWindows) {
    & (Join-Path $PSScriptRoot 'package.ps1') -Configuration $Configuration -Runtime win-x64
}
