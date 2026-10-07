#!/bin/zsh
set -euo pipefail

PROJECT_ROOT="${0:A:h:h}"
BUILD_ROOT="$PROJECT_ROOT/.build/dmg"
PRODUCT_DIR="$BUILD_ROOT/products"
STAGE_DIR="$BUILD_ROOT/stage"
OUTPUT_DIR="$PROJECT_ROOT/dist"
APP_PATH="$PRODUCT_DIR/Myra.app"
DMG_PATH="$OUTPUT_DIR/Myra.dmg"

zsh "$PROJECT_ROOT/Scripts/prepare-vlc.sh"

# Keep the last distributable recoverable until the replacement is validated.
if [[ -f "$DMG_PATH" ]]; then
  cp -p "$DMG_PATH" "$OUTPUT_DIR/Myra.previous.dmg"
fi

rm -rf "$BUILD_ROOT"
mkdir -p "$PRODUCT_DIR" "$STAGE_DIR" "$OUTPUT_DIR"

xcodebuild -quiet \
  -project "$PROJECT_ROOT/Myra.xcodeproj" \
  -scheme Myra \
  -configuration Release \
  -destination "platform=macOS,arch=arm64" \
  -derivedDataPath "$BUILD_ROOT/DerivedData" \
  CONFIGURATION_BUILD_DIR="$PRODUCT_DIR" \
  CODE_SIGNING_ALLOWED=NO \
  build

mkdir -p "$APP_PATH/Contents/Resources/Licenses"
cp "$PROJECT_ROOT/Vendor/COPYING.txt" "$APP_PATH/Contents/Resources/Licenses/VLCKit-LGPL-2.1.txt"

codesign --force --deep --sign - "$APP_PATH"
codesign --verify --deep --strict --verbose=2 "$APP_PATH"
ditto "$APP_PATH" "$STAGE_DIR/Myra.app"
ln -s /Applications "$STAGE_DIR/Applications"

rm -f "$DMG_PATH"
hdiutil create \
  -volname "Myra" \
  -srcfolder "$STAGE_DIR" \
  -format UDZO \
  -ov \
  "$DMG_PATH" >/dev/null

echo "Created $DMG_PATH"
hdiutil verify "$DMG_PATH"
shasum -a 256 "$DMG_PATH"
