#!/bin/zsh
set -euo pipefail

# MobileVLCKit is the iOS/iPadOS build of VLCKit (same libvlc revision as prepare-vlc.sh).
PROJECT_ROOT="${0:A:h:h}"
VENDOR_ROOT="$PROJECT_ROOT/Vendor"
FRAMEWORK="$VENDOR_ROOT/MobileVLCKit.xcframework"
ARCHIVE="$PROJECT_ROOT/.build/dependencies/MobileVLCKit-3.7.3.tar.xz"
ARCHIVE_URL="https://download.videolan.org/pub/cocoapods/prod/MobileVLCKit-3.7.3-319ed2c0-79128878.tar.xz"
ARCHIVE_SHA256="0d04059906962ddc9a7bd1ebaa12e1f9ae85eb2466116a97a2f46886dd27a0a9"

if [[ -f "$FRAMEWORK/Info.plist" ]]; then
  exit 0
fi
mkdir -p "${ARCHIVE:h}" "$VENDOR_ROOT"
if [[ ! -f "$ARCHIVE" ]]; then
  curl --fail --location --retry 2 --connect-timeout 20 --max-time 1200 \
    --output "$ARCHIVE" "$ARCHIVE_URL"
fi
ACTUAL_SHA256="$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')"
if [[ "$ACTUAL_SHA256" != "$ARCHIVE_SHA256" ]]; then
  print -u2 "MobileVLCKit archive checksum mismatch; extraction stopped."
  exit 1
fi
tar -xf "$ARCHIVE" -C "$VENDOR_ROOT" --strip-components 1 \
  'MobileVLCKit-binary/MobileVLCKit.xcframework'
print "Prepared MobileVLCKit 3.7.3"
