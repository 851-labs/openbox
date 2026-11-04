#!/usr/bin/env bash
# Builds the FFmpeg the AirPlay receiver needs (AirPlay/): only the AAC (with AAC-ELD) and ALAC
# audio decoders, as static libraries for MSVC. Run from an MSYS2 shell that has the Visual Studio
# x64 tools on its PATH (CI does: .github/workflows/app.yml).
#
#   tools/build_ffmpeg.sh <prefix>
#
# FFmpeg is LGPL-2.1-or-later; built this way (no GPL or non-free parts) it can go in openbox.
set -euo pipefail

VERSION=n7.1.5
prefix=${1:?usage: build_ffmpeg.sh <prefix>}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

git clone --quiet --depth 1 --branch "$VERSION" https://github.com/FFmpeg/FFmpeg.git "$work/ffmpeg"
cd "$work/ffmpeg"
# -MT: the static C runtime, as airplay.dll is built (AirPlay/CMakeLists.txt).
./configure --toolchain=msvc --arch=x86_64 --target-os=win32 --prefix="$prefix" \
    --enable-static --disable-shared --disable-autodetect --disable-everything \
    --disable-programs --disable-doc --disable-network --disable-debug --disable-x86asm \
    --disable-avdevice --disable-avformat --disable-avfilter --disable-swscale --disable-swresample \
    --enable-decoder=aac,alac --extra-cflags=-MT
make -j"$(nproc)"
make install
