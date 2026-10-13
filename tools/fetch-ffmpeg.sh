#!/usr/bin/env bash
# Downloads an LGPL build of ffmpeg for Windows (BtbN/FFmpeg-Builds, static, no GPL parts) into
# artifacts/ffmpeg/win-x64: ffmpeg.exe, its LICENSE.txt and FFMPEG-SOURCE.txt (where its source is). The checksum is checked against the
# release's checksums.sha256. Linux builds use the system's ffmpeg instead.
# Usage: tools/fetch-ffmpeg.sh [series, e.g. 8.1]
set -euo pipefail

SERIES="${1:-8.1}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/ffmpeg/win-x64"
BASE="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest"
NAME="ffmpeg-n$SERIES-latest-win64-lgpl-$SERIES"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

curl -fsSL -o "$WORK/$NAME.zip" "$BASE/$NAME.zip"
curl -fsSL -o "$WORK/checksums.sha256" "$BASE/checksums.sha256"
(cd "$WORK" && grep " $NAME.zip\$" checksums.sha256 | sha256sum -c -)

unzip -q "$WORK/$NAME.zip" -d "$WORK/x"
rm -rf "$OUT" && mkdir -p "$OUT"
cp "$WORK/x/$NAME/bin/ffmpeg.exe" "$OUT/"
cp "$WORK/x/$NAME/LICENSE.txt" "$OUT/"
cat > "$OUT/FFMPEG-SOURCE.txt" <<SOURCE
This folder contains ffmpeg from FFmpeg (https://ffmpeg.org), licensed under the GNU Lesser General
Public License version 2.1 or later (see LICENSE.txt). My Home Security runs it as a separate
program; My Home Security itself is MIT-licensed.

Build:  $NAME
        from $BASE/$NAME.zip
        built by https://github.com/BtbN/FFmpeg-Builds (build scripts and the exact
        configuration of every bundled library are in that repository)
Source: FFmpeg release branch release/$SERIES at https://github.com/FFmpeg/FFmpeg/tree/release/$SERIES
        (also https://ffmpeg.org/download.html#releases)

You may obtain the complete corresponding source code of this FFmpeg build, including the
libraries it was built with, from the locations above. If they are unavailable, ask via
https://github.com/StuMP90/my-home-security/issues and it will be provided, for at least three
years after this release.
SOURCE
echo "ffmpeg $SERIES (LGPL) is in $OUT"
