[CmdletBinding()]
param(
    [string] $WorkspacePath = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $DeskName = "flyt-acceptance-$(Get-Date -Format 'yyyyMMdd-HHmmss')",
    [string] $Template,
    [string] $ReceiptPath,
    [switch] $AllowSmallTerminal,
    [switch] $KeepDesk
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repositoryRoot
try {
    dotnet restore FlytV1.slnx
    dotnet build FlytV1.slnx --configuration Release --no-restore
    dotnet test FlytV1.slnx --configuration Release --no-build

    $flytDll = Join-Path $repositoryRoot 'src/Flyt.Cli/bin/Release/net10.0/flyt.dll'
    $arguments = @(
        $flytDll
        'doctor'
        '--interactive'
        '--name', $DeskName
        '--path', (Resolve-Path -LiteralPath $WorkspacePath).Path
    )
    if ($Template) { $arguments += @('--template', $Template) }
    if ($ReceiptPath) { $arguments += @('--receipt', $ReceiptPath) }
    if ($AllowSmallTerminal) { $arguments += '--allow-small-terminal' }
    if ($KeepDesk) { $arguments += '--keep-desk' }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Flyt target-host acceptance exited with code $LASTEXITCODE. Inspect the generated receipt."
    }
}
finally {
    Pop-Location
}
