#!/bin/bash
# Build a local Apple Silicon NetFluss.app. No Apple Developer ID is required.
set -euo pipefail
cd "$(dirname "$0")/.."
swift build -c release
BIN="$(swift build -c release --show-bin-path)"
DEST="${1:-$PWD/NetFluss.app}"
mkdir -p "$DEST/Contents/MacOS" "$DEST/Contents/Resources" \
  "$DEST/Contents/Frameworks" "$DEST/Contents/Library/HelperTools" \
  "$DEST/Contents/Library/LaunchDaemons"
cp "$BIN/Netfluss" "$DEST/Contents/MacOS/NetFluss"
cp "$BIN/NetflussPrivilegedHelper" "$DEST/Contents/Library/HelperTools/NetflussPrivilegedHelper"
cp Packaging/Info.plist "$DEST/Contents/Info.plist"
cp LICENSE "$DEST/Contents/Resources/LICENSE"
cp Packaging/LaunchDaemons/com.local.netfluss.privilegedhelper.plist \
  "$DEST/Contents/Library/LaunchDaemons/"
cp Packaging/Resources/AppIcon*.icns "$DEST/Contents/Resources/"
cp -R Packaging/Resources/SpeedTest "$DEST/Contents/Resources/"
cp -R Packaging/Resources/*.lproj "$DEST/Contents/Resources/"
ditto "$BIN/Sparkle.framework" "$DEST/Contents/Frameworks/Sparkle.framework"
if [[ "${INCLUDE_VPN_TOOLS:-0}" == "1" ]]; then
  INTEL=0 ./Packaging/VPN/build-vpn-bundle.sh "$DEST/Contents/Library/VPN"
fi
xattr -cr "$DEST"
if [[ -d "$DEST/Contents/Library/VPN" ]]; then
  for tool in "$DEST"/Contents/Library/VPN/*; do
    if file -b "$tool" | grep -q 'Mach-O'; then
      codesign --force --sign - "$tool"
    fi
  done
fi
codesign --force --sign - "$DEST/Contents/Library/HelperTools/NetflussPrivilegedHelper"
codesign --force --sign - --entitlements Netfluss.entitlements "$DEST"
codesign --verify --deep --strict --verbose=2 "$DEST"
echo "Built $DEST"
