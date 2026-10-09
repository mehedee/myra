#!/bin/zsh
set -euo pipefail
PROJECT_ROOT="${0:A:h:h}"
cd "$PROJECT_ROOT"
zsh Scripts/prepare-vlc.sh

SWIFT_FILES=(Package.swift Sources/Myra/*.swift Tests/MyraTests/*.swift)
xcrun swift-format format --in-place "${SWIFT_FILES[@]}"
xcrun swift-format lint --strict "${SWIFT_FILES[@]}"
plutil -lint Myra.xcodeproj/project.pbxproj
mkdir -p .build/verification/ui
export MYRA_UI_SNAPSHOT_DIR="$PROJECT_ROOT/.build/verification/ui"

if command -v ffmpeg >/dev/null; then
  mkdir -p .build/verification
  ffmpeg -nostdin -y -loglevel error \
    -f lavfi -i 'color=c=blue:s=64x36:r=5:d=20' \
    -f lavfi -i 'sine=frequency=440:sample_rate=44100:duration=20' \
    -f lavfi -i 'sine=frequency=660:sample_rate=44100:duration=20' \
    -i Tests/MyraTests/Fixtures/English.srt \
    -map 0:v -map 1:a -map 2:a -map 3:s \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -c:s srt \
    -metadata:s:a:0 language=fra -metadata:s:a:1 language=eng -metadata:s:s:0 language=eng \
    -t 20 .build/verification/synthetic.mkv
  export MYRA_VLC_TEST_MEDIA="$PROJECT_ROOT/.build/verification/synthetic.mkv"
  mkdir -p .build/verification/workflow/Series
  for EPISODE in 1 2; do
    ffmpeg -nostdin -y -loglevel error \
      -f lavfi -i 'color=c=blue:s=160x90:r=10:d=3' \
      -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
      ".build/verification/workflow/Series/Episode${EPISODE}.mkv"
  done
  # French is deliberately the container default: the app must override it with English.
  ffmpeg -nostdin -y -loglevel error \
    -f lavfi -i 'color=c=blue:s=160x90:r=10:d=20' \
    -f lavfi -i 'sine=frequency=440:sample_rate=44100:duration=20' \
    -f lavfi -i 'sine=frequency=660:sample_rate=44100:duration=20' \
    -i Tests/MyraTests/Fixtures/English.srt -i Tests/MyraTests/Fixtures/English.srt \
    -map 0:v -map 1:a -map 2:a -map 3:s -map 4:s \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -c:s srt \
    -metadata:s:a:0 language=fra -metadata:s:a:1 language=eng \
    -metadata:s:s:0 language=fra -metadata:s:s:1 language=eng \
    -disposition:a:0 default -disposition:a:1 0 \
    -disposition:s:0 default -disposition:s:1 0 \
    -t 20 .build/verification/workflow/TrackDefaults.mkv
fi

# SwiftPM links VLCKit but does not embed it inside the macOS test bundle.
# Its install name resolves relative to the test binary, just as in the app.
swift build --build-tests
TEST_PRODUCTS_DIR="$(swift build --show-bin-path)"
TEST_FRAMEWORKS_DIR="$TEST_PRODUCTS_DIR/MyraTests.xctest/Contents/Frameworks"
mkdir -p "$TEST_FRAMEWORKS_DIR"
ditto "$PROJECT_ROOT/Vendor/VLCKit.xcframework/macos-arm64_x86_64/VLCKit.framework" \
  "$TEST_FRAMEWORKS_DIR/VLCKit.framework"
ditto "$PROJECT_ROOT/.build/artifacts/sparkle/Sparkle/Sparkle.xcframework/macos-arm64_x86_64/Sparkle.framework" \
  "$TEST_FRAMEWORKS_DIR/Sparkle.framework"
swift test --skip-build
zsh Scripts/build-dmg.sh
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' .build/dmg/products/Myra.app/Contents/Info.plist)" == "3.0.2" ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' .build/dmg/products/Myra.app/Contents/Info.plist)" == "10" ]]
codesign --verify --deep --strict --verbose=2 .build/dmg/products/Myra.app
otool -L .build/dmg/products/Myra.app/Contents/MacOS/Myra
print "Myra update verification and DMG build completed."
