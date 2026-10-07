$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'global.json') | ConvertFrom-Json).sdk.version
$sdkDirectory = Join-Path $env:LOCALAPPDATA 'DubbingApp\dotnet'
New-Item -ItemType Directory -Force -Path $sdkDirectory | Out-Null
$installer = Join-Path $sdkDirectory 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
& $installer -Version $version -InstallDir $sdkDirectory -NoPath
if ($LASTEXITCODE -ne 0) { throw 'No se pudo instalar el SDK local.' }
