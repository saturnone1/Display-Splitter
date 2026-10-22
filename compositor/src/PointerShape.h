#pragma once
#include <dxgi1_2.h>
#include <cstdint>
#include <cstring>
#include <span>
#include <vector>

namespace ds {
// Converts a DXGI pointer shape to tightly packed BGRA. Returns false when the
// shape type is unknown or the buffer is too small.
inline bool ConvertPointerShape(const DXGI_OUTDUPL_POINTER_SHAPE_INFO& info,
                         std::span<const uint8_t> raw,
                         std::vector<uint8_t>& outBgra,
                         UINT& outWidth, UINT& outHeight) {
    const UINT pitch = info.Pitch;
    if (pitch == 0 || info.Width == 0 || info.Height == 0) return false;

    // Pitch must hold a complete row, not merely fit the supplied buffer.
    // Reject malformed metadata before allocation or reading either cursor mask.
    const bool monochrome = info.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME;
    const bool color = info.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR
        || info.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR;
    if (!monochrome && !color) return false;
    if (monochrome && (info.Height < 2 || info.Height % 2 != 0)) return false;
    const uint64_t rowBytes = monochrome ? (static_cast<uint64_t>(info.Width) + 7) / 8
        : static_cast<uint64_t>(info.Width) * 4;
    if (rowBytes > pitch || static_cast<uint64_t>(pitch) * info.Height > raw.size()) return false;
    const uint64_t pixels = static_cast<uint64_t>(info.Width) * (monochrome ? info.Height / 2 : info.Height);
    if (pixels > outBgra.max_size() / 4) return false;

    switch (info.Type) {
    case DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR: {
        outWidth = info.Width;
        outHeight = info.Height;
        if (raw.size() < static_cast<size_t>(pitch) * outHeight) return false;
        outBgra.resize(static_cast<size_t>(outWidth) * outHeight * 4);
        for (UINT y = 0; y < outHeight; ++y) {
            memcpy(&outBgra[static_cast<size_t>(y) * outWidth * 4],
                   &raw[static_cast<size_t>(y) * pitch],
                   static_cast<size_t>(outWidth) * 4);
        }
        return true;
    }

    case DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME: {
        // Top half = AND mask, bottom half = XOR mask, 1 bit per pixel.
        outWidth = info.Width;
        outHeight = info.Height / 2;
        if (outHeight == 0) return false;
        if (raw.size() < static_cast<size_t>(pitch) * info.Height) return false;
        outBgra.resize(static_cast<size_t>(outWidth) * outHeight * 4);
        for (UINT y = 0; y < outHeight; ++y) {
            const uint8_t* andRow = &raw[static_cast<size_t>(y) * pitch];
            const uint8_t* xorRow = &raw[static_cast<size_t>(y + outHeight) * pitch];
            for (UINT x = 0; x < outWidth; ++x) {
                const uint8_t bit = static_cast<uint8_t>(0x80 >> (x & 7));
                const bool a = (andRow[x >> 3] & bit) != 0;
                const bool b = (xorRow[x >> 3] & bit) != 0;
                uint8_t* px = &outBgra[(static_cast<size_t>(y) * outWidth + x) * 4];
                if (!a && !b) {           // opaque black
                    px[0] = px[1] = px[2] = 0x00; px[3] = 0xFF;
                } else if (!a && b) {     // opaque white
                    px[0] = px[1] = px[2] = 0xFF; px[3] = 0xFF;
                } else if (a && !b) {     // transparent
                    px[0] = px[1] = px[2] = px[3] = 0x00;
                } else {                  // screen invert - approximated as white
                    px[0] = px[1] = px[2] = 0xFF; px[3] = 0xFF;
                }
            }
        }
        return true;
    }

    case DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR: {
        // 32bpp; the alpha byte is a mask: 0 = replace screen, 0xFF = XOR.
        outWidth = info.Width;
        outHeight = info.Height;
        if (raw.size() < static_cast<size_t>(pitch) * outHeight) return false;
        outBgra.resize(static_cast<size_t>(outWidth) * outHeight * 4);
        for (UINT y = 0; y < outHeight; ++y) {
            const uint8_t* src = &raw[static_cast<size_t>(y) * pitch];
            for (UINT x = 0; x < outWidth; ++x) {
                const uint8_t* s = src + static_cast<size_t>(x) * 4;
                uint8_t* px = &outBgra[(static_cast<size_t>(y) * outWidth + x) * 4];
                if (s[3] == 0) {
                    px[0] = s[0]; px[1] = s[1]; px[2] = s[2]; px[3] = 0xFF;
                } else {
                    // XOR-with-screen is approximated by inverting the colour.
                    px[0] = static_cast<uint8_t>(~s[0]);
                    px[1] = static_cast<uint8_t>(~s[1]);
                    px[2] = static_cast<uint8_t>(~s[2]);
                    px[3] = 0xFF;
                }
            }
        }
        return true;
    }

    default:
        return false;
    }
}

} // namespace ds
