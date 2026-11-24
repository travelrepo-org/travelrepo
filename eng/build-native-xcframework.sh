#!/usr/bin/env sh
# Builds TravelRepo.xcframework for iOS devices and the iOS simulator (arm64) from TravelRepo.Native:
#
#   eng/build-native-xcframework.sh <output-directory>
#
# Each slice is one static library (TravelRepo, the .NET runtime and its native parts merged with libtool) with the
# header and a module map. Swift imports it as CTravelRepo; the module map links the system libraries and frameworks it
# needs. Needs macOS with Xcode and the .NET workload mobile-librarybuilder (see docs/native.md).
set -eu
OUTPUT=$1
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK="$OUTPUT/work"
rm -rf "$OUTPUT"; mkdir -p "$WORK"

ARGS=""
for RUNTIME in ios-arm64 iossimulator-arm64; do
  "$ROOT/eng/build-native.sh" "$RUNTIME" "$WORK/$RUNTIME"
  mkdir -p "$WORK/$RUNTIME/merged/Headers"
  # TravelRepo and the bootstrapper that starts the .NET runtime become one object: an archive member nothing refers to
  # would be left out by the app's linker, and the runtime would never start.
  LIB="$WORK/$RUNTIME/lib"
  xcrun ld -r -keep_private_externs -arch arm64 -o "$WORK/$RUNTIME/TravelRepo.o" -all_load "$LIB/libTravelRepoNative.a" "$LIB/libbootstrapperdll.o"
  rm "$LIB/libTravelRepoNative.a" "$LIB/libbootstrapperdll.o"
  libtool -static -no_warning_for_no_symbols -o "$WORK/$RUNTIME/merged/libTravelRepo.a" "$WORK/$RUNTIME/TravelRepo.o" "$LIB"/*.a
  cp "$ROOT/src/TravelRepo.Native/include/travelrepo.h" "$WORK/$RUNTIME/merged/Headers/"
  cat > "$WORK/$RUNTIME/merged/Headers/module.modulemap" <<'MAP'
module CTravelRepo {
    header "travelrepo.h"
    link "z"
    link "icucore"
    link "objc"
    link framework "CoreFoundation"
    link framework "CryptoKit"
    link framework "Foundation"
    link framework "GSS"
    link framework "Network"
    link framework "Security"
    export *
}
MAP
  ARGS="$ARGS -library $WORK/$RUNTIME/merged/libTravelRepo.a -headers $WORK/$RUNTIME/merged/Headers"
done
# shellcheck disable=SC2086
xcodebuild -create-xcframework $ARGS -output "$OUTPUT/TravelRepo.xcframework"
rm -rf "$WORK"
echo "TravelRepo.xcframework in $OUTPUT"
