#!/usr/bin/env sh
# Compiles TravelRepo.Native with NativeAOT into a static library for one platform and collects what a native app
# links with it:
#
#   eng/build-native.sh <runtime> <output-directory>       runtime: ios-arm64, iossimulator-arm64, osx-arm64, ...
#
# The output directory then holds libTravelRepoNative.a, the .NET runtime libraries it needs, include/travelrepo.h
# and link-flags.txt with the system libraries and frameworks to link. See docs/native.md.
#
# Needs macOS with Xcode for Apple platforms. For iOS, the .NET workload mobile-librarybuilder must be installed
# (dotnet workload install mobile-librarybuilder --skip-manifest-update); it provides the NativeAOT runtime for iOS.
set -eu
RUNTIME=$1
OUTPUT=$2
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DOTNET=${DOTNET:-dotnet}
PUBLISH="$OUTPUT/publish"

rm -rf "$OUTPUT"; mkdir -p "$OUTPUT/lib" "$OUTPUT/include"
"$DOTNET" publish "$ROOT/eng/native/TravelRepo.NativeLibrary.csproj" -c Release -p:NativeRuntime="$RUNTIME" -o "$PUBLISH" -nodeReuse:false
cp "$PUBLISH/TravelRepoNative.a" "$OUTPUT/lib/libTravelRepoNative.a"
cp "$ROOT/src/TravelRepo.Native/include/travelrepo.h" "$OUTPUT/include/"

VERSION=$("$DOTNET" msbuild "$ROOT/eng/native/TravelRepo.NativeLibrary.csproj" -getProperty:BundledNETCoreAppPackageVersion -p:NativeRuntime="$RUNTIME")
PACKAGES=${NUGET_PACKAGES:-$HOME/.nuget/packages}
case $RUNTIME in
  ios*) NATIVE="$PACKAGES/microsoft.netcore.app.runtime.nativeaot.$RUNTIME/$VERSION/runtimes/$RUNTIME/native" ;;
  *) NATIVE="$PACKAGES/runtime.$RUNTIME.microsoft.dotnet.ilcompiler/$VERSION" ;;
esac
# The libraries the ILCompiler's own link step uses for NativeLib=Static (Microsoft.NETCore.Native.Unix.targets).
for library in libbootstrapperdll.o libRuntime.WorkstationGC.a libeventpipe-disabled.a libstandalonegc-disabled.a libaotminipal.a \
  libstdc++compat.a libSystem.Globalization.Native.a libSystem.Native.a libSystem.IO.Compression.Native.a libSystem.Net.Security.Native.a \
  libSystem.Security.Cryptography.Native.Apple.a libbrotlienc.a libbrotlidec.a libbrotlicommon.a; do
  found=$(find "$NATIVE" -name "$library" | head -n 1)
  [ -n "$found" ] || { echo "Missing $library under $NATIVE" >&2; exit 1; }
  cp "$found" "$OUTPUT/lib/"
done
# libicucore is the system's ICU: cultures, sorting and formats without shipping ICU data.
echo "-lz -licucore -lobjc -lm -framework CoreFoundation -framework CryptoKit -framework Foundation -framework Network -framework Security -framework GSS" > "$OUTPUT/link-flags.txt"
rm -rf "$PUBLISH"
echo "TravelRepo.Native for $RUNTIME in $OUTPUT"
