// Live slot routing. Capture threads and GPU resources keep their original indices.
#pragma once
#include "Common.h"
#include <atomic>

namespace ds {
constexpr wchar_t kLiveLayoutMessage[] = L"DesktopSplitter.LiveLayout.v1";
constexpr uint32_t kLayoutSignature = 0x44530000;

inline uint32_t IdentityRouting(size_t count) {
    uint32_t value = kLayoutSignature | (static_cast<uint32_t>(count) << 8);
    for (size_t i = 0; i < count && i < 4; ++i)
        value |= static_cast<uint32_t>(i) << (i * 2);
    return value;
}

inline bool ValidRouting(uint32_t value, size_t count) {
    if (count < 1 || count > 4 || (value & 0xFFFFFF00u) != (kLayoutSignature | (static_cast<uint32_t>(count) << 8))) return false;
    uint32_t seen = 0;
    for (size_t i = 0; i < count; ++i) {
        const uint32_t index = (value >> (i * 2)) & 3;
        if (index >= count || (seen & (1u << index))) return false;
        seen |= 1u << index;
    }
    return (value & 0xFFu) >> (count * 2) == 0;
}

inline bool ExchangeRouting(std::atomic<uint32_t>& routing, size_t count, uint32_t expected, uint32_t desired) {
    return ValidRouting(desired, count) && routing.compare_exchange_strong(expected, desired, std::memory_order_acq_rel);
}

inline uint32_t LayoutFingerprint(const AppConfig& config) {
    // Same UTF-16 FNV-1a sequence as the control app; rejects a stale config file.
    uint32_t hash = 2166136261u;
    auto add = [&hash](const std::wstring& text) {
        for (wchar_t c : text) { hash ^= static_cast<uint16_t>(c); hash *= 16777619u; }
    };
    add(config.physicalDevice + L"|");
    for (const auto& segment : config.segments) {
        add(segment.virtualDevice + L"," + std::to_wstring(segment.width) + L"," + std::to_wstring(segment.height)
            + L"," + std::to_wstring(segment.physRect.x) + L"," + std::to_wstring(segment.physRect.y)
            + L"," + std::to_wstring(segment.physRect.w) + L"," + std::to_wstring(segment.physRect.h) + L";");
    }
    return hash;
}
} // namespace ds
