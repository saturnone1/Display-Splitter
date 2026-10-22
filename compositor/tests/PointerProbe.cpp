// Pure cursor conversion checks. No device, capture, window or display operations.
#include "../src/PointerShape.h"
#include <algorithm>
#include <cstdio>
#include <limits>

int main() {
    int checks = 0, failures = 0;
    auto check = [&](bool ok, const char* name) {
        ++checks;
        if (!ok) { ++failures; std::printf("FAIL %s\n", name); }
    };
    DXGI_OUTDUPL_POINTER_SHAPE_INFO info{};
    info.Type = DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR;
    info.Width = 2; info.Height = 2; info.Pitch = 12;
    const std::vector<uint8_t> color = {1,2,3,4, 5,6,7,8, 99,99,99,99,
        9,10,11,12, 13,14,15,16, 99,99,99,99};
    std::vector<uint8_t> output;
    UINT width = 0, height = 0;
    auto convert = [&](const std::vector<uint8_t>& bytes) {
        return ds::ConvertPointerShape(info, bytes, output, width, height);
    };
    check(convert(color) && width == 2 && height == 2 && output == std::vector<uint8_t>({1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16}),
        "padded color rows");
    auto* storage = output.data();
    std::fill(output.begin(), output.end(), uint8_t{0xEE});
    check(convert(color) && output.data() == storage && output.back() == 16, "reused buffer fully overwritten");
    const auto previous = output;
    info.Pitch = 4;
    check(!convert(color) && output == previous, "short color pitch rejected before modifying output");
    info.Pitch = 12;
    check(!convert(std::vector<uint8_t>(23)), "truncated color buffer");
    info.Width = (std::numeric_limits<UINT>::max)(); info.Pitch = (std::numeric_limits<UINT>::max)();
    check(!convert(color), "row byte arithmetic does not wrap");
    info.Type = DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME;
    info.Width = 4; info.Height = 2; info.Pitch = 1;
    check(convert({0x30, 0x50}) && width == 4 && height == 1
        && output == std::vector<uint8_t>({0,0,0,255, 255,255,255,255, 0,0,0,0, 255,255,255,255}), "AND/XOR masks");
    info.Width = 9;
    check(!convert({0,0}), "short monochrome pitch");
    info.Width = 4; info.Height = 3;
    check(!convert({0,0,0}), "odd monochrome height");
    info.Height = 1;
    check(!convert({0}), "missing second mask");
    info.Type = DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR;
    info.Width = 2; info.Height = 1; info.Pitch = 8;
    check(convert({1,2,3,0, 10,20,30,255})
        && output == std::vector<uint8_t>({1,2,3,255, 245,235,225,255}), "masked color");
    info.Pitch = 4;
    check(!convert(std::vector<uint8_t>(8)), "short masked-color pitch");
    info.Pitch = 8; info.Type = 99;
    check(!convert(color), "unknown shape type");
    info.Type = DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR; info.Width = 0;
    check(!convert(color), "zero width");
    info.Width = 2; info.Height = 0;
    check(!convert(color), "zero height");
    info.Height = 1; info.Pitch = 0;
    check(!convert(color), "zero pitch");
    std::printf("PointerProbe: %d checks, %d failures; no display access.\n", checks, failures);
    return failures ? 1 : 0;
}
