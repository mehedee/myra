#!/bin/zsh
set -euo pipefail

PROJECT_ROOT="${0:A:h:h}"
VENDOR_ROOT="$PROJECT_ROOT/Vendor"
FRAMEWORK="$VENDOR_ROOT/VLCKit.xcframework"
ARCHIVE="$PROJECT_ROOT/.build/dependencies/VLCKit-3.7.3.tar.xz"
ARCHIVE_URL="https://download.videolan.org/cocoapods/prod/VLCKit-3.7.3-319ed2c0-79128878.tar.xz"
ARCHIVE_SHA256="019afdae4e2e2d0f3ac325fac8f7ba0af25dca70b9d157df7d60db88e0be8e5d"

if [[ -f "$FRAMEWORK/Info.plist" ]]; then
  exit 0
fi
mkdir -p "${ARCHIVE:h}" "$VENDOR_ROOT"
curl --fail --location --retry 2 --connect-timeout 20 --max-time 600 \
  --output "$ARCHIVE" "$ARCHIVE_URL"
ACTUAL_SHA256="$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')"
if [[ "$ACTUAL_SHA256" != "$ARCHIVE_SHA256" ]]; then
  print -u2 "VLCKit archive checksum mismatch; extraction stopped."
  exit 1
fi
tar -xf "$ARCHIVE" -C "$VENDOR_ROOT" --strip-components 1 \
  'VLCKit - binary package/VLCKit.xcframework' 'VLCKit - binary package/COPYING.txt'
print "Prepared VLCKit 3.7.3"
