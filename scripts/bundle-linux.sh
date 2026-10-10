#!/usr/bin/env bash
set -euo pipefail
package=$(realpath "$1")
root=$(cd "$(dirname "$0")/.." && pwd)
cache="$root/artifacts/downloads"
appdir="$package.AppDir"
mkdir -p "$cache" "$appdir/usr/lib/opendub" "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/scalable/apps"
cp -a "$package/." "$appdir/usr/lib/opendub/"
# Optional .NET LTTng tracing provider depends on an obsolete host ABI; EventPipe and normal execution do not need it.
rm -f -- "$appdir/usr/lib/opendub/libcoreclrtraceptprovider.so"
mkdir -p "$appdir/usr/lib/vlc"
cp -L /usr/lib/x86_64-linux-gnu/libvlc.so.5 /usr/lib/x86_64-linux-gnu/libvlccore.so.9 "$appdir/usr/lib/"
cp -a /usr/lib/x86_64-linux-gnu/vlc/plugins "$appdir/usr/lib/vlc/"
ln -sf ../lib/opendub/OpenDub "$appdir/usr/bin/OpenDub"
ln -sf libvlc.so.5 "$appdir/usr/lib/libvlc.so"
cp "$root/scripts/opendub.svg" "$appdir/usr/share/icons/hicolor/scalable/apps/opendub.svg"
cat > "$appdir/usr/share/applications/opendub.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=OpenDub
Exec=OpenDub
Icon=opendub
Categories=AudioVideo;AudioVideoEditing;
Terminal=false
EOF
cat > "$cache/OpenDub-AppRun" <<'EOF'
#!/usr/bin/env bash
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
export LD_LIBRARY_PATH="$HERE/usr/lib:$HERE/usr/lib/opendub:${LD_LIBRARY_PATH:-}"
export VLC_PLUGIN_PATH="$HERE/usr/lib/vlc/plugins"
exec "$HERE/usr/lib/opendub/OpenDub" "$@"
EOF
chmod +x "$cache/OpenDub-AppRun" "$appdir/usr/lib/opendub/OpenDub"
tool="$cache/linuxdeploy-x86_64.AppImage"
hash=8aea8da0f7f7039d2a2cecb14657d752a222a5e1d3825caeef186c82f751cdd1
if ! echo "$hash  $tool" | sha256sum -c --status; then
    curl --fail --location --retry 3 -H 'Accept: application/octet-stream' 'https://api.github.com/repos/linuxdeploy/linuxdeploy/releases/assets/602438599' -o "$tool"
fi
echo "$hash  $tool" | sha256sum -c
chmod +x "$tool"
export APPIMAGE_EXTRACT_AND_RUN=1
"$tool" --appdir "$appdir" --deploy-deps-only "$appdir/usr/lib/opendub" --deploy-deps-only "$appdir/usr/lib/vlc/plugins" --library /usr/lib/x86_64-linux-gnu/libvlc.so.5 --library /usr/lib/x86_64-linux-gnu/libvlccore.so.9 --desktop-file "$appdir/usr/share/applications/opendub.desktop" --icon-file "$appdir/usr/share/icons/hicolor/scalable/apps/opendub.svg" --custom-apprun "$cache/OpenDub-AppRun"
export OUTPUT="$package.AppImage"
mkdir -p "$appdir/usr/lib/opendub/licenses/ubuntu"
dpkg-query -W -f='${binary:Package}\t${Version}\t${source:Package}\t${source:Version}\n' > "$appdir/usr/lib/opendub/licenses/ubuntu/packages.tsv"
# Retain distro copyright notices, including dependencies copied by linuxdeploy.
for doc in /usr/share/doc/*/copyright; do
    component=$(basename "$(dirname "$doc")")
    cp -L "$doc" "$appdir/usr/lib/opendub/licenses/ubuntu/$component.copyright" 2>/dev/null || true
done
"$tool" --appdir "$appdir" --custom-apprun "$cache/OpenDub-AppRun" --output appimage
