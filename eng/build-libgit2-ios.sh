#!/usr/bin/env sh
# Builds libgit2 for iOS and iPadOS as git2.xcframework for TravelRepo.Git.LibGit2: one framework for devices (arm64)
# and one for the simulator (arm64 and x86_64).
#
#   eng/build-libgit2-ios.sh <libgit2-source> <output-directory>
#
# Runs on macOS with Xcode and CMake. The source must be the commit LibGit2Sharp.NativeBinaries is built from
# (5853918 for 2.0.324, libgit2 1.8.6), the same as for Android. iOS apps may only load libraries packaged as
# frameworks, so LibGit2Sharp does not find this one by its usual name (git2-5853918): the app passes the path of
# git2.framework/git2 to LibGit2Backend.UseNativeLibrary.
#
# iOS has zlib, so the system's is used; the copy bundled with libgit2 does not compile against current iOS SDKs
# (its zutil.h turns fdopen into a macro that stdio.h then declares).
#
# As on Android, HTTPS and SSH are left out: TravelRepo.Git.LibGit2 talks HTTP(S) through .NET's HttpClient, so the
# system's TLS and certificates are used.
set -eu
SOURCE=$1
OUTPUT=$2
CMAKE=${CMAKE:-cmake}
MINIMUM=15.0
VERSION=1.8.6

framework() {
  NAME=$1 SDK=$2 ARCHS=$3 PLATFORM=$4
  BUILD="$OUTPUT/build/$NAME"
  "$CMAKE" -S "$SOURCE" -B "$BUILD" -G "Unix Makefiles" \
    -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT="$SDK" -DCMAKE_OSX_ARCHITECTURES="$ARCHS" -DCMAKE_OSX_DEPLOYMENT_TARGET=$MINIMUM \
    -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DBUILD_TESTS=OFF -DBUILD_CLI=OFF -DBUILD_EXAMPLES=OFF \
    -DUSE_HTTPS=OFF -DUSE_SSH=OFF -DUSE_NTLMCLIENT=OFF -DUSE_GSSAPI=OFF \
    -DUSE_BUNDLED_ZLIB=OFF -DREGEX_BACKEND=builtin -DUSE_HTTP_PARSER=builtin \
    -DLIBGIT2_FILENAME=git2-5853918
  "$CMAKE" --build "$BUILD" --parallel
  DIR="$OUTPUT/build/$NAME/git2.framework"
  rm -rf "$DIR" && mkdir -p "$DIR"
  # The versioned file, not its symbolic links: a framework holds a single binary named after the framework.
  LIBRARY=$(find "$BUILD" -maxdepth 1 -name 'libgit2-5853918*.dylib' -type f | head -n 1)
  cp "$LIBRARY" "$DIR/git2"
  install_name_tool -id @rpath/git2.framework/git2 "$DIR/git2"
  # Keeps the exported functions LibGit2Sharp calls, drops the rest of the symbol table.
  xcrun strip -x "$DIR/git2"
  cat > "$DIR/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>git2</string>
  <key>CFBundleIdentifier</key><string>org.libgit2.git2</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>git2</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleSupportedPlatforms</key><array><string>$PLATFORM</string></array>
  <key>MinimumOSVersion</key><string>$MINIMUM</string>
</dict>
</plist>
PLIST
}

framework device iphoneos arm64 iPhoneOS
framework simulator iphonesimulator "arm64;x86_64" iPhoneSimulator
rm -rf "$OUTPUT/git2.xcframework"
xcodebuild -create-xcframework \
  -framework "$OUTPUT/build/device/git2.framework" \
  -framework "$OUTPUT/build/simulator/git2.framework" \
  -output "$OUTPUT/git2.xcframework"
