# TS resolution probe harness

This harness calls the exported `TsInfo_ReadFile` C API, which delegates to the
production `TsInfo::ReadFileFromC` path. The fixture generator creates all TS
container data from null packets, synthetic PAT/PMT tables, and the small
synthetic MPEG2 elementary stream checked into `fixtures/`.

On Linux, install a C++17 compiler, Python 3, FFmpeg development packages
(`libavcodec`, `libavformat`, `libavutil`, `libswscale`, `libswresample`),
OpenSSL, and zlib, then run:

```sh
AmatsukazeTsInfoProbeHarness/build_linux.sh
```

Set `TSINFO_PROBE_BUILD_DIR` to move the generated objects and fixtures. The
build compiles the production TS, PSI, PES, MPEG2, H.264, HEVC, and supporting
bitstream translation units. `test_support/avisynth.h` supplies only types used
by unrelated Avisynth headers that are included transitively; none of its
methods participate in TS parsing or fixture assertions.

The matrix covers delayed HD metadata and PES crossing the 4 MiB input boundary,
zero-height and confirmed low-resolution MPEG2, PMT-advertised but unavailable
video, stale-format clearing after a PMT PID change, scrambled video, shared
video PID across services, audio-only input without EIT, and exhaustion of the
200 MiB actual-byte budget.
