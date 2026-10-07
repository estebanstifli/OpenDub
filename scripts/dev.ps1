param(
    [ValidateSet('Build', 'Test', 'Run', 'Restore')]
    [string]$Action = 'Build',
    [switch]$MediaTests
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localSdk = if ($IsWindows) { Join-Path $env:LOCALAPPDATA 'DubbingApp/dotnet/dotnet.exe' } else { '/nonexistent' }
$sdkExecutable = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path -Parent $sdkExecutable
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if ($MediaTests) { $env:FFMPEG_TESTS = '1' }
Push-Location -LiteralPath $projectRoot
try {
    switch ($Action) {
        'Build' { & $sdkExecutable build DubbingApp.sln -c Release }
        'Test' { & $sdkExecutable test DubbingApp.sln -c Release }
        'Run' { & $sdkExecutable run --project src/Dubbing.App -c Release }
        'Restore' { & $sdkExecutable restore DubbingApp.sln --locked-mode }
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet $Action falló con código $LASTEXITCODE." }
}
finally { Pop-Location }
