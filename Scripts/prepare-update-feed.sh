#!/bin/zsh
# Prepare locally only. This script never uploads or publishes a release.
set -euo pipefail
PROJECT_ROOT="${0:A:h:h}"
cd "$PROJECT_ROOT"
APP_PATH="$PROJECT_ROOT/.build/dmg/products/Myra.app"
DMG_PATH="$PROJECT_ROOT/dist/Myra.dmg"
TOOLS="$PROJECT_ROOT/.build/artifacts/sparkle/Sparkle/bin"
[[ -f "$DMG_PATH" && -d "$APP_PATH" ]] || { print -u2 "Build Myra.dmg first."; exit 1; }
[[ -x "$TOOLS/generate_appcast" ]] || { print -u2 "Resolve the Swift packages first."; exit 1; }
VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP_PATH/Contents/Info.plist")"
[[ "$VERSION" == <->.<->.<-> ]] || { print -u2 "Invalid release version."; exit 1; }
PUBLIC_KEY="$("$TOOLS/generate_keys" --account com.mehedee.Myra -p)"
BUNDLE_KEY="$(/usr/libexec/PlistBuddy -c 'Print :SUPublicEDKey' "$APP_PATH/Contents/Info.plist")"
[[ "$PUBLIC_KEY" == "$BUNDLE_KEY" ]] || { print -u2 "Keychain signing key does not match the built app."; exit 1; }
codesign --verify --deep --strict "$APP_PATH"
hdiutil verify "$DMG_PATH"
mkdir -p "$PROJECT_ROOT/.build/update-feed" "$PROJECT_ROOT/dist/update"
FEED_STAGE="$(mktemp -d "$PROJECT_ROOT/.build/update-feed/release.XXXXXX")"
cp -p "$DMG_PATH" "$FEED_STAGE/Myra.dmg"
cp "$PROJECT_ROOT/docs/release-notes-$VERSION.md" "$FEED_STAGE/Myra.md"
"$TOOLS/generate_appcast" --account com.mehedee.Myra \
  --download-url-prefix "https://github.com/mehedee/h5ai-streamer/releases/download/v$VERSION/" \
  --link "https://github.com/mehedee/h5ai-streamer/releases" \
  --maximum-deltas 0 --embed-release-notes "$FEED_STAGE"
[[ -f "$FEED_STAGE/appcast.xml" ]] || { print -u2 "No appcast was generated."; exit 1; }
"$TOOLS/sign_update" --account com.mehedee.Myra --verify "$FEED_STAGE/appcast.xml"
ditto "$FEED_STAGE" "$PROJECT_ROOT/dist/update"
(cd "$PROJECT_ROOT/dist/update" && shasum -a 256 Myra.dmg > Myra.dmg.sha256)
print "Prepared signed update files in dist/update. Nothing was published."
