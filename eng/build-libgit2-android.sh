#!/usr/bin/env sh
# Builds libgit2 for Android (arm64-v8a and x86_64) for TravelRepo.Git.LibGit2.
#
#   eng/build-libgit2-android.sh <libgit2-source> <output-directory>
#
# The source must be the libgit2 commit LibGit2Sharp.NativeBinaries is built from (5853918 for 2.0.324, libgit2
# 1.8.6), because LibGit2Sharp loads the library by that name: lib/<abi>/libgit2-5853918.so.
#
# Needs ANDROID_NDK (r28 or later) and CMake with Ninja, for example from the Android SDK
# ("ndk;28.2.13676358" and "cmake;3.31.6").
#
# HTTPS is left out on purpose: TravelRepo.Git.LibGit2 talks HTTP(S) through .NET's HttpClient, so Android's own
# TLS and certificate store are used and no OpenSSL is shipped. SSH is not supported.
set -eu
SOURCE=$1
OUTPUT=$2
NDK=${ANDROID_NDK:?Set ANDROID_NDK to the NDK folder}
CMAKE=${CMAKE:-cmake}
STRIP="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip"

for ABI in arm64-v8a x86_64; do
  BUILD="$OUTPUT/build/$ABI"
  # libgit2 compiles as C90, where the NDK's Linux headers do not parse "inline"; __inline__ works there.
  # max-page-size keeps the library loadable on devices with 16 KB pages (required from Android 16).
  "$CMAKE" -S "$SOURCE" -B "$BUILD" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" -DANDROID_ABI="$ABI" -DANDROID_PLATFORM=android-29 \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_FLAGS=-Dinline=__inline__ -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-z,max-page-size=16384" \
    -DBUILD_SHARED_LIBS=ON -DBUILD_TESTS=OFF -DBUILD_CLI=OFF -DBUILD_EXAMPLES=OFF \
    -DUSE_HTTPS=OFF -DUSE_SSH=OFF -DUSE_NTLMCLIENT=OFF -DUSE_GSSAPI=OFF \
    -DUSE_BUNDLED_ZLIB=ON -DREGEX_BACKEND=builtin -DUSE_HTTP_PARSER=builtin \
    -DLIBGIT2_FILENAME=git2-5853918
  "$CMAKE" --build "$BUILD"
  mkdir -p "$OUTPUT/$ABI"
  "$STRIP" --strip-unneeded -o "$OUTPUT/$ABI/libgit2-5853918.so" "$BUILD/libgit2-5853918.so"
done
