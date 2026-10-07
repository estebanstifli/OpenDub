param(
    [ValidateSet('win-x64','linux-x64','osx-arm64')][string]$Runtime = 'win-x64',
    [switch]$SkipRuntimes,
    [switch]$Archive,
    [string]$PackageName,
    [switch]$AudioCuda
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tar-helpers.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot 'artifacts/releases'
if (!$PackageName) { $PackageName = "OpenDub-0.1.0-$Runtime" }
if ($PackageName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_.-]*$') { throw 'PackageName must be a simple directory name.' }
$package = Join-Path $releaseRoot $PackageName
$output = if ($Runtime -eq 'osx-arm64') { Join-Path $package 'OpenDub.app/Contents/MacOS' } else { $package }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sdk = if ($IsWindows -and (Test-Path -LiteralPath "$env:LOCALAPPDATA/DubbingApp/dotnet/dotnet.exe")) { "$env:LOCALAPPDATA/DubbingApp/dotnet/dotnet.exe" } else { (Get-Command dotnet).Source }
& $sdk publish (Join-Path $projectRoot 'src/Dubbing.App/Dubbing.App.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:RestoreLockedMode=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
# The NuGet lock is shared across hosts; trim unused Windows VLC architectures only inside this output.
$vlcRoot = [IO.Path]::GetFullPath((Join-Path $output 'libvlc'))
if (!$vlcRoot.StartsWith([IO.Path]::GetFullPath($package) + [IO.Path]::DirectorySeparatorChar)) { throw 'Invalid package path.' }
if (Test-Path -LiteralPath $vlcRoot) {
    if ($Runtime -ne 'win-x64') { Remove-Item -LiteralPath $vlcRoot -Recurse -Force }
    else { foreach ($arch in @('win-x86','win-arm64')) { $unused = Join-Path $vlcRoot $arch; if (Test-Path -LiteralPath $unused) { Remove-Item -LiteralPath $unused -Recurse -Force } } }
}
if (!$SkipRuntimes) { & (Join-Path $PSScriptRoot 'install-runtimes.ps1') -Runtime $Runtime -Destination $output -AudioCuda:$AudioCuda }
if ($Runtime -eq 'win-x64') { New-Item -ItemType File -Path (Join-Path $output 'portable.mode') -Force | Out-Null }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $package -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'DubbingApp_PROJECT_PLAN.md') -Destination $package -Force
if ($Runtime -eq 'osx-arm64') {
    $plist = @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>CFBundleName</key><string>OpenDub</string><key>CFBundleIdentifier</key><string>com.opendub.studio</string><key>CFBundleVersion</key><string>0.1.0</string><key>CFBundleShortVersionString</key><string>0.1.0</string><key>CFBundleExecutable</key><string>OpenDub</string><key>CFBundlePackageType</key><string>APPL</string><key>NSHighResolutionCapable</key><true/></dict></plist>
'@
    [IO.File]::WriteAllText((Join-Path $package 'OpenDub.app/Contents/Info.plist'), $plist)
}
if ($Runtime -eq 'osx-arm64' -and $IsMacOS) { & (Join-Path $PSScriptRoot 'bundle-macos.ps1') -Package $package }
if (!$IsWindows) { & chmod +x (Join-Path $output 'OpenDub') }
$checksums = Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object Name -ne 'SHA256SUMS' | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/') }
$checksums | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS') -Encoding utf8
if ($Archive) {
    if ($Runtime -eq 'win-x64') { Compress-Archive -LiteralPath $package -DestinationPath ($package + '.zip') -Force }
    else { Compress-PortableTar -Directory $package -Archive ($package + '.tar.gz') }
}
if ($Runtime -eq 'linux-x64' -and $IsLinux) { & bash (Join-Path $PSScriptRoot 'bundle-linux.sh') $package; if ($LASTEXITCODE -ne 0) { throw 'AppImage bundling failed.' } }
Write-Output "Package: $package"
