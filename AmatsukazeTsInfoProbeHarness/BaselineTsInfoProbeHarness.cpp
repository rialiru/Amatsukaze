#include "TsInfo.h"

#include <iostream>
#include <string>

extern "C" int TsInfo_ReadFile(TsInfo *ptr, const tchar *filepath);
extern "C" int TsInfo_GetNumProgram(TsInfo *ptr);
extern "C" void TsInfo_GetVideoFormat(TsInfo *ptr, int index, int *stream, int *width, int *height, int *sarW, int *sarH);

int main(int argc, char** argv) {
    if (argc != 2) {
        std::cerr << "usage: BaselineTsInfoProbeHarness metadata-first-delayed-hd.ts\n";
        return 2;
    }
    AMTContext context;
    TsInfo info(context);
    const std::string fixturePath = std::string(argv[1]) + "/metadata-first-delayed-hd.ts";
    const bool readOk = TsInfo_ReadFile(&info, fixturePath.c_str()) != 0;
    int stream = -1, width = -1, height = -1, sarW = 0, sarH = 0;
    const int programs = TsInfo_GetNumProgram(&info);
    if (programs > 0) {
        TsInfo_GetVideoFormat(&info, 0, &stream, &width, &height, &sarW, &sarH);
    }
    std::cout << "BASELINE_PUBLIC_C_API readOk=" << (readOk ? "true" : "false")
        << " programs=" << programs << " stream=" << stream
        << " width=" << width << " height=" << height << '\n';
    const bool reproduced = readOk && programs == 1 && width == 0 && height == 0;
    std::cout << (reproduced ? "PASS " : "FAIL ")
        << "unmodified 899bcf8 returns success before delayed HD dimensions\n";
    return reproduced ? 0 : 1;
}
