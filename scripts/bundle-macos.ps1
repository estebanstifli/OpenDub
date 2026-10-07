param([Parameter(Mandatory)][string]$Package)
$ErrorActionPreference = 'Stop'
if (!$IsMacOS) { throw 'Build the DMG on macOS.' }
$cache = Join-Path $PSScriptRoot '../artifacts/downloads'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$dmg = Join-Path $cache 'vlc-3.0.24-arm64.dmg'
$hash = '64a89d93cdd30b0e97131743e246373db82d6826ea882d265d46d74b136da2b7'
if (!(Test-Path -LiteralPath $dmg) -or (Get-FileHash -LiteralPath $dmg -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
    Invoke-WebRequest 'https://download.videolan.org/vlc/3.0.24/macosx/vlc-3.0.24-arm64.dmg' -OutFile $dmg
}
if ((Get-FileHash -LiteralPath $dmg -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'VLC checksum mismatch.' }
$mount = Join-Path $cache ('vlc-mount-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $mount | Out-Null
& hdiutil attach $dmg -readonly -nobrowse -mountpoint $mount
if ($LASTEXITCODE -ne 0) { throw 'VLC mount failed.' }
try {
    $frameworks = Join-Path $Package 'OpenDub.app/Contents/Frameworks'
    New-Item -ItemType Directory -Path $frameworks -Force | Out-Null
    & ditto (Join-Path $mount 'VLC.app') (Join-Path $frameworks 'VLC.app')
    if ($LASTEXITCODE -ne 0) { throw 'VLC bundle copy failed.' }
} finally { & hdiutil detach $mount }
# Private previews are explicitly unsigned. Release signing can use an installed Developer ID identity.
if ($env:OPENDUB_SIGN_IDENTITY) {
    & codesign --force --deep --options runtime --sign $env:OPENDUB_SIGN_IDENTITY (Join-Path $Package 'OpenDub.app')
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
}
& hdiutil create -volname OpenDub -srcfolder $Package -ov -format UDZO ($Package + '.dmg')
if ($LASTEXITCODE -ne 0) { throw 'DMG creation failed.' }
if ($env:OPENDUB_NOTARY_PROFILE) {
    & xcrun notarytool submit ($Package + '.dmg') --keychain-profile $env:OPENDUB_NOTARY_PROFILE --wait
    if ($LASTEXITCODE -ne 0) { throw 'Notarization failed.' }
    & xcrun stapler staple ($Package + '.dmg')
    if ($LASTEXITCODE -ne 0) { throw 'Stapling failed.' }
}
