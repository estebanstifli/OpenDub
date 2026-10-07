param([switch]$FromSource)
& (Join-Path $PSScriptRoot 'Run-OpenDub.ps1') -FromSource:$FromSource
