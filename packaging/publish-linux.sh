#!/usr/bin/env bash
# Builds a self-contained Linux tarball: artifacts/linux/myhomesecurity-<version>-linux-<arch>.tar.gz
# ffmpeg isn't bundled: the app uses the system's (install it with your package manager).
# Usage: packaging/publish-linux.sh [version] [x64|arm64]
set -euo pipefail

VERSION="${1:-0.1.0}"
ARCH="${2:-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/linux"
STAGE="$OUT/myhomesecurity-$VERSION-linux-$ARCH"

rm -rf "$STAGE"
dotnet publish "$ROOT/src/MyHomeSecurity.App/MyHomeSecurity.App.csproj" -c Release -r "linux-$ARCH" --self-contained true \
  -p:Version="$VERSION" -o "$STAGE/lib"

# Licence texts for everything shipped (shown in the app under Help → Licences).
python3 "$ROOT/tools/generate-third-party-notices.py" --rid "linux-$ARCH" --output "$STAGE/lib/THIRD-PARTY-NOTICES.txt"
cp "$ROOT/LICENSE" "$STAGE/lib/LICENSE.txt"
cp "$STAGE/lib/THIRD-PARTY-NOTICES.txt" "$STAGE/lib/LICENSE.txt" "$STAGE/"
cp "$ROOT/packaging/linux/myhomesecurity.png" "$STAGE/myhomesecurity.png"

cat > "$STAGE/install.sh" <<'INSTALL'
#!/usr/bin/env bash
# Installs for the current user into ~/.local
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
APPS=~/.local/share/applications
ICONS=~/.local/share/icons/hicolor/256x256/apps
mkdir -p ~/.local/lib ~/.local/bin "$APPS" "$ICONS"
rm -rf ~/.local/lib/myhomesecurity && cp -r "$HERE/lib" ~/.local/lib/myhomesecurity
ln -sf ~/.local/lib/myhomesecurity/MyHomeSecurity ~/.local/bin/myhomesecurity

# The icon goes in first, so a new menu entry never shows a generic icon. Exec and Icon are
# absolute, so the entry works even when ~/.local/bin isn't on the session's PATH.
cp "$HERE/myhomesecurity.png" "$ICONS/myhomesecurity.png"
touch ~/.local/share/icons/hicolor
cat > "$APPS/myhomesecurity.desktop.tmp" <<DESKTOP
[Desktop Entry]
Type=Application
Name=My Home Security
Comment=Watch your cameras and control Home Assistant, privately
Exec=$HOME/.local/lib/myhomesecurity/MyHomeSecurity
Icon=$ICONS/myhomesecurity.png
StartupWMClass=MyHomeSecurity
Terminal=false
Categories=AudioVideo;Video;Network;
Keywords=camera;cctv;tapo;rtsp;onvif;home assistant;security;
DESKTOP
mv "$APPS/myhomesecurity.desktop.tmp" "$APPS/myhomesecurity.desktop"
if command -v update-desktop-database >/dev/null; then update-desktop-database "$APPS" 2>/dev/null || true; fi
command -v ffmpeg >/dev/null || echo "Note: ffmpeg isn't installed. Install it (e.g. 'sudo apt install ffmpeg') to view cameras."
echo "Installed. Run 'myhomesecurity' or find My Home Security in your applications menu."
INSTALL
chmod +x "$STAGE/install.sh"

tar -C "$OUT" -czf "$STAGE.tar.gz" "$(basename "$STAGE")"
echo "Created $STAGE.tar.gz"
