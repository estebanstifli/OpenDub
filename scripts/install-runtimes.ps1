param(
    [ValidateSet('win-x64','linux-x64','osx-arm64')][string]$Runtime = 'win-x64',
    [Parameter(Mandatory)][string]$Destination,
    [string]$LinuxCudaRuntime,
    [switch]$AudioCuda
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tar-helpers.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'runtimes/versions.json') -Raw | ConvertFrom-Json -AsHashtable
$cache = Join-Path $projectRoot 'artifacts/downloads'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$runtimeRoot = Join-Path ([IO.Path]::GetFullPath($Destination)) "runtimes/$Runtime"
$assets = @($manifest.platforms[$Runtime])
if ($AudioCuda) {
    if ($Runtime -ne 'win-x64') { throw 'The optional CUDA package currently targets win-x64.' }
    $assets += $manifest.accelerators['win-x64-cuda']
    $assets += $manifest.accelerators['win-x64-llama-cuda']
}
if ($Runtime -eq 'linux-x64') {
    $assets += $manifest.accelerators['linux-x64-vulkan']
    if ($LinuxCudaRuntime) { $assets += $manifest.accelerators['linux-x64-llama-cuda']; $assets += $manifest.accelerators['linux-x64-cuda-extra'] }
}
foreach ($asset in $assets) {
    $download = Join-Path $cache $asset.name
    if (!(Test-Path -LiteralPath $download) -or (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
        $temporary = $download + '.partial'
        Invoke-WebRequest -Uri $asset.url -OutFile $temporary
        if ((Get-Item -LiteralPath $temporary).Length -ne $asset.size -or (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
            throw "Checksum mismatch: $($asset.name)"
        }
        Move-Item -LiteralPath $temporary -Destination $download -Force
    }
    if ($asset.component -eq 'audio-cuda-extra') {
        $target = Join-Path $runtimeRoot 'audio-cuda'
        $wheel = Join-Path $target 'licenses/nvidia-nvjitlink'
        [IO.Compression.ZipFile]::ExtractToDirectory($download, $wheel, $true)
        Get-ChildItem -LiteralPath $wheel -Recurse -File -Filter 'libnvJitLink.so*' | ForEach-Object {
            Move-Item -LiteralPath $_.FullName -Destination (Join-Path $target $_.Name) -Force
        }
    } elseif ($asset.component -in @('audio','audio-cuda','audio-vulkan','llama','llama-cuda')) {
        $target = Join-Path $runtimeRoot $asset.component
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        if ($asset.name.EndsWith('.zip')) { Expand-Archive -LiteralPath $download -DestinationPath $target -Force }
        else { Expand-PortableTar -Archive $download -Destination $target }
    } else {
        $target = Join-Path $runtimeRoot 'ffmpeg'
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        $name = switch ($asset.component) {
            'ffmpeg-license' { 'LICENSE.txt' }
            'ffmpeg-readme' { 'BUILD-SOURCES.txt' }
            default { $asset.component + $(if ($Runtime -eq 'win-x64') { '.exe' } else { '' }) }
        }
        Copy-Item -LiteralPath $download -Destination (Join-Path $target $name) -Force
    }
}
if ($LinuxCudaRuntime) {
    if ($Runtime -ne 'linux-x64' -or !(Test-Path -LiteralPath (Join-Path $LinuxCudaRuntime 'audiocpp_cli'))) { throw 'Invalid Linux CUDA runtime.' }
    $cudaTarget = Join-Path $runtimeRoot 'audio-cuda'
    New-Item -ItemType Directory -Force -Path $cudaTarget | Out-Null
    Get-ChildItem -LiteralPath $LinuxCudaRuntime | Copy-Item -Destination $cudaTarget -Recurse -Force
}
if (!$IsWindows) {
    Get-ChildItem -LiteralPath $runtimeRoot -File -Recurse | Where-Object { $_.Name -match '^(audiocpp_|llama-|ffmpeg$|ffprobe$)' } | ForEach-Object { & chmod +x $_.FullName }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'runtimes/versions.json') -Destination (Join-Path $runtimeRoot 'versions.json') -Force
Write-Output "Verified runtimes installed: $runtimeRoot"
