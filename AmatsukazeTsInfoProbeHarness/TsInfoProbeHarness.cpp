#include "TsInfo.h"

#include <cstdlib>
#include <iostream>
#include <string>

extern "C" int TsInfo_ReadFile(TsInfo *ptr, const tchar *filepath);
extern "C" int TsInfo_GetResolutionProbeStatus(TsInfo *ptr);
extern "C" int TsInfo_GetNumProgram(TsInfo *ptr);
extern "C" void TsInfo_GetProgramInfo(TsInfo *ptr, int index, int *programId, int *hasVideo, int *videoPid, int *numContent);
extern "C" void TsInfo_GetVideoFormat(TsInfo *ptr, int index, int *stream, int *width, int *height, int *sarW, int *sarH);

namespace {
bool check(bool condition, const std::string& name) {
    std::cout << (condition ? "PASS " : "FAIL ") << name << '\n';
    return condition;
}

bool probe(AMTContext& context, const std::string& path, bool readOk,
    TsInfoResolutionProbeStatus status, int expectedWidth, int expectedHeight,
    bool expectAdvertisedVideo, const std::string& name, int expectedPrograms = -1) {
    TsInfo info(context);
    const bool actualReadOk = TsInfo_ReadFile(&info, path.c_str()) != 0;
    bool ok = check(actualReadOk == readOk, name + " ReadFileFromC");
    const auto actualStatus = static_cast<TsInfoResolutionProbeStatus>(TsInfo_GetResolutionProbeStatus(&info));
    ok &= check(actualStatus == status, name + " resolution status");
    const int programs = TsInfo_GetNumProgram(&info);
    if (expectedPrograms >= 0) {
        ok &= check(programs == expectedPrograms, name + " program count");
    }
    bool foundVideo = false;
    bool foundFormat = false;
    bool matchedExpectedSize = false;
    for (int i = 0; i < programs; ++i) {
        int programId = 0, hasVideo = 0, videoPid = -1, numContent = 0;
        int stream = 0, width = 0, height = 0, sarW = 0, sarH = 0;
        TsInfo_GetProgramInfo(&info, i, &programId, &hasVideo, &videoPid, &numContent);
        TsInfo_GetVideoFormat(&info, i, &stream, &width, &height, &sarW, &sarH);
        foundVideo |= hasVideo != 0;
        foundFormat |= stream != VS_UNKNOWN && width > 0 && height > 0;
        if (expectedWidth > 0 && width == expectedWidth && height == expectedHeight) {
            matchedExpectedSize = true;
        }
    }
    ok &= check(foundVideo == expectAdvertisedVideo, name + " advertised video metadata");
    ok &= check(foundFormat == (expectedWidth > 0 && expectedHeight > 0), name + " usable dimensions");
    if (expectedWidth > 0 && expectedHeight > 0) {
        ok &= check(matchedExpectedSize, name + " expected dimensions");
    }
    return ok;
}
}

int main(int argc, char** argv) {
    if (argc != 2) {
        std::cerr << "usage: TsInfoProbeHarness fixture-directory\n";
        return 2;
    }
    const std::string directory(argv[1]);
    AMTContext context;
    bool ok = true;
    ok &= probe(context, directory + "/hd-delayed.ts", true,
        TsInfoResolutionProbeStatus::Resolved, 1440, 1080, true, "HD delayed past 4 MiB head read");
    ok &= probe(context, directory + "/metadata-first-delayed-hd.ts", true,
        TsInfoResolutionProbeStatus::Resolved, 1440, 1080, true, "metadata and EIT precede delayed HD sequence");
    ok &= probe(context, directory + "/hd-window-edge.ts", true,
        TsInfoResolutionProbeStatus::Resolved, 1440, 1080, true, "HD PES crossing 4 MiB read boundary");
    ok &= probe(context, directory + "/low-resolution.ts", true,
        TsInfoResolutionProbeStatus::Resolved, 320, 1080, true, "confirmed low resolution");
    ok &= probe(context, directory + "/zero-height.ts", true,
        TsInfoResolutionProbeStatus::UnknownResolution, 0, 0, true, "zero height remains unresolved");
    ok &= probe(context, directory + "/advertised-video-unknown.ts", true,
        TsInfoResolutionProbeStatus::UnknownResolution, 0, 0, true, "advertised but unavailable video PID");
    ok &= probe(context, directory + "/pmt-pid-change.ts", true,
        TsInfoResolutionProbeStatus::UnknownResolution, 0, 0, true, "PMT video PID change clears stale format");
    ok &= probe(context, directory + "/scrambled-video.ts", false,
        TsInfoResolutionProbeStatus::UnknownResolution, 0, 0, true, "scrambled video stays unresolved");
    ok &= probe(context, directory + "/multiple-services.ts", true,
        TsInfoResolutionProbeStatus::Resolved, 1440, 1080, true, "shared video PID across services", 2);
    ok &= probe(context, directory + "/audio-only-no-eit.ts", false,
        TsInfoResolutionProbeStatus::UnknownResolution, 0, 0, false, "audio only without EIT", 1);
    ok &= probe(context, directory + "/budget-exhausted.ts", true,
        TsInfoResolutionProbeStatus::ReadBudgetExhausted, 0, 0, true, "200 MiB actual-byte read budget");
    ok &= probe(context, directory + "/short-first-window.ts", true,
        TsInfoResolutionProbeStatus::UnknownResolution, 0, 0, true, "head region remains bounded after midpoint EOF");
    return ok ? 0 : 1;
}
