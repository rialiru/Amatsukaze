#!/bin/sh
set -eu

HARNESS_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SOURCE_ROOT=${TSINFO_PROBE_SOURCE_ROOT:-$(CDPATH= cd -- "$HARNESS_DIR/.." && pwd)}
BUILD_DIR=${TSINFO_PROBE_BUILD_DIR:-/tmp/amatsukaze-tsinfo-probe-build}
FIXTURE_DIR=${1:-"$BUILD_DIR/fixtures"}
HARNESS_SOURCE=${TSINFO_PROBE_HARNESS_SOURCE:-"$HARNESS_DIR/TsInfoProbeHarness.cpp"}
CXX=${CXX:-g++}
mkdir -p "$BUILD_DIR/objects" "$FIXTURE_DIR"

if ! pkg-config --exists libavcodec libavformat libavutil libswscale libswresample; then
    echo "FFmpeg development packages are required (libavcodec/libavformat/libavutil/libswscale/libswresample)." >&2
    exit 2
fi
FFMPEG_CFLAGS=$(pkg-config --cflags libavcodec libavformat libavutil libswscale libswresample)
COMMON_FLAGS="-std=c++17 -O0 -ffunction-sections -fdata-sections -DAMATSUKAZE2DLL -DWINAPI= -I$HARNESS_DIR/test_support -I$SOURCE_ROOT/Amatsukaze -I$SOURCE_ROOT/include -I$SOURCE_ROOT/include_gpl -I$SOURCE_ROOT/common -I/usr/include $FFMPEG_CFLAGS"

compile_source() {
    source_file=$1
    object_file=$2
    extra_flags=${3:-}
    # Intentional word splitting for compiler flags returned by pkg-config.
    # shellcheck disable=SC2086
    "$CXX" $COMMON_FLAGS $extra_flags -c "$source_file" -o "$object_file"
}

for source_file in TsInfo Mpeg2TsParser TsSplitter Mpeg2VideoParser H264VideoParser HEVCVideoParser AribString FileUtils StringUtils OSUtil ReaderWriterFFmpeg; do
    compile_source "$SOURCE_ROOT/Amatsukaze/$source_file.cpp" "$BUILD_DIR/objects/$source_file.o"
done
compile_source "$SOURCE_ROOT/common/rgy_codepage.cpp" "$BUILD_DIR/objects/rgy_codepage.o"
compile_source "$SOURCE_ROOT/common/rgy_filesystem.cpp" "$BUILD_DIR/objects/rgy_filesystem.o"
compile_source "$SOURCE_ROOT/common/rgy_util.cpp" "$BUILD_DIR/objects/rgy_util.o"
compile_source "$SOURCE_ROOT/common/rgy_bitstream.cpp" "$BUILD_DIR/objects/rgy_bitstream.o"
compile_source "$SOURCE_ROOT/common/rgy_memmem.cpp" "$BUILD_DIR/objects/rgy_memmem.o"
compile_source "$SOURCE_ROOT/common/rgy_simd.cpp" "$BUILD_DIR/objects/rgy_simd.o"
compile_source "$SOURCE_ROOT/common/rgy_bitstream_avx2.cpp" "$BUILD_DIR/objects/rgy_bitstream_avx2.o" "-mavx2"
compile_source "$SOURCE_ROOT/common/rgy_bitstream_avx512bw.cpp" "$BUILD_DIR/objects/rgy_bitstream_avx512bw.o" "-mavx512bw"
compile_source "$SOURCE_ROOT/common/rgy_memmem_avx2.cpp" "$BUILD_DIR/objects/rgy_memmem_avx2.o" "-mavx2"
compile_source "$SOURCE_ROOT/common/rgy_memmem_avx512bw.cpp" "$BUILD_DIR/objects/rgy_memmem_avx512bw.o" "-mavx512bw"

# Intentional word splitting for the FFmpeg flags returned by pkg-config.
# shellcheck disable=SC2086
"$CXX" $COMMON_FLAGS "$HARNESS_SOURCE" "$BUILD_DIR"/objects/*.o \
    -Wl,--gc-sections -lavcodec -lavutil -lswscale -lswresample -lavformat -lcrypto -lz -ldl -lpthread \
    -o "$BUILD_DIR/TsInfoProbeHarness"
python3 "$HARNESS_DIR/generate_fixtures.py" "$FIXTURE_DIR"
"$BUILD_DIR/TsInfoProbeHarness" "$FIXTURE_DIR"
