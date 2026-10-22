// Live routing v2: nine four-bit indices, count in bits 36..39, signature in bits 40..63.
#pragma once
#include "Common.h"
#include "../../shared/DeskSplitProtocol.h"
#include <atomic>

namespace ds {
constexpr wchar_t kLiveLayoutMessage[] = L"DesktopSplitter.LiveLayout.v2";
constexpr uint64_t kLayoutSignature = 0x4453000000000000ull;

inline uint64_t IdentityRouting(size_t count) {
    if (count < 1 || count > DESKSPLIT_MAX_MONITORS) return 0;
    uint64_t value = kLayoutSignature | (static_cast<uint64_t>(count) << 36);
    for (size_t i = 0; i < count; ++i) value |= static_cast<uint64_t>(i) << (i * 4);
    return value;
}

inline bool ValidRouting(uint64_t value, size_t count) {
    if (count < 1 || count > DESKSPLIT_MAX_MONITORS
        || (value & 0xFFFFFFF000000000ull) != (kLayoutSignature | (static_cast<uint64_t>(count) << 36))) return false;
    uint32_t seen = 0;
    for (size_t i = 0; i < count; ++i) {
        const uint32_t index = static_cast<uint32_t>((value >> (i * 4)) & 15);
        if (index >= count || (seen & (1u << index))) return false;
        seen |= 1u << index;
    }
    return (value & 0xFFFFFFFFFull) >> (count * 4) == 0;
}

inline bool ExchangeRouting(std::atomic<uint64_t>& routing, size_t count, uint64_t expected, uint64_t desired) {
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
