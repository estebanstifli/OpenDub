param([switch]$FromSource)
$releases = Join-Path $PSScriptRoot 'artifacts/releases'
$latest = Get-ChildItem -LiteralPath $releases -Directory -Filter 'OpenDub-*' -ErrorAction SilentlyContinue |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'OpenDub.exe') } |
    Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'OpenDub.exe')).LastWriteTimeUtc } -Descending | Select-Object -First 1
if (!$FromSource -and $latest) {
    Start-Process -FilePath (Join-Path $latest.FullName 'OpenDub.exe') -WorkingDirectory $latest.FullName -WindowStyle Hidden
} else { & (Join-Path $PSScriptRoot 'scripts/dev.ps1') -Action Run }
