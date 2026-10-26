#pragma once

// Type-only compatibility shim for parser translation units that include the
// unrelated Avisynth-backed core headers. The native TS probe never calls it.
#include <cstdint>

#define PLANAR_Y 1
#define PLANAR_U 2
#define PLANAR_V 4

class IScriptEnvironment2 {};

class PVideoFrame {
public:
    PVideoFrame* operator->() { return this; }
    const PVideoFrame* operator->() const { return this; }
    const std::uint8_t* GetReadPtr(int) const { return nullptr; }
    int GetPitch(int) const { return 0; }
};
