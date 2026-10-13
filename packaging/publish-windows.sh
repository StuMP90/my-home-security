#!/usr/bin/env bash
# Builds a self-contained Windows zip (from Linux or Windows/Git Bash):
# artifacts/windows/myhomesecurity-<version>-win-x64.zip, with an LGPL ffmpeg in an "ffmpeg" folder.
# Usage: packaging/publish-windows.sh [version]
set -euo pipefail

VERSION="${1:-0.1.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/windows"
STAGE="$OUT/myhomesecurity-$VERSION-win-x64"
FFMPEG="$ROOT/artifacts/ffmpeg/win-x64"

[ -f "$FFMPEG/ffmpeg.exe" ] && [ -f "$FFMPEG/FFMPEG-SOURCE.txt" ] || "$ROOT/tools/fetch-ffmpeg.sh"

rm -rf "$STAGE" "$STAGE.zip"
dotnet publish "$ROOT/src/MyHomeSecurity.App/MyHomeSecurity.App.csproj" -c Release -r win-x64 --self-contained true \
  -p:Version="$VERSION" -o "$STAGE"
mkdir -p "$STAGE/ffmpeg"
cp "$FFMPEG/ffmpeg.exe" "$FFMPEG/LICENSE.txt" "$FFMPEG/FFMPEG-SOURCE.txt" "$STAGE/ffmpeg/"

python3 "$ROOT/tools/generate-third-party-notices.py" --rid win-x64 --ffmpeg "$FFMPEG" --output "$STAGE/THIRD-PARTY-NOTICES.txt"
cp "$ROOT/LICENSE" "$STAGE/LICENSE.txt"

(cd "$OUT" && zip -qr "$(basename "$STAGE").zip" "$(basename "$STAGE")")
echo "Created $STAGE.zip"
