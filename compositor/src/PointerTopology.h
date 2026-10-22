#pragma once
#include "Geometry.h"
#include "LiveLayout.h"
#include <algorithm>

namespace ds {
// Desktop rectangles are indexed by capture source; local rectangles by visible
// slot. Routing joins them, including after a whole-display number swap.
struct PointerTopology {
    RECT covered{};
    std::vector<RECT> sources;
    std::vector<RECT> slots;
    std::vector<RECT> others;
};

inline LONG MapAxis(LONG value, LONG fromStart, LONG fromEnd, LONG toStart, LONG toEnd) {
    const auto from = static_cast<int64_t>(fromEnd) - fromStart;
    const auto to = static_cast<int64_t>(toEnd) - toStart;
    if (from <= 1 || to <= 1) return toStart;
    const auto offset = std::clamp<int64_t>(static_cast<int64_t>(value) - fromStart, 0, from - 1);
    return static_cast<LONG>(toStart + offset * (to - 1) / (from - 1));
}

inline POINT MapPoint(POINT p, const RECT& from, const RECT& to) {
    return { MapAxis(p.x, from.left, from.right, to.left, to.right),
             MapAxis(p.y, from.top, from.bottom, to.top, to.bottom) };
}

inline bool ValidPointerTopology(const PointerTopology& t, uint64_t routing) {
    if (!(!RectIsEmpty(t.covered) && t.sources.size() == t.slots.size()
        && ValidRouting(routing, t.slots.size())
        && std::none_of(t.sources.begin(), t.sources.end(), RectIsEmpty)
        && std::none_of(t.slots.begin(), t.slots.end(), RectIsEmpty))) return false;
    const LONG width = t.covered.right - t.covered.left, height = t.covered.bottom - t.covered.top;
    long long area = 0;
    for (const auto& r : t.slots) {
        if (r.left < 0 || r.top < 0 || r.right > width || r.bottom > height) return false;
        area += RectArea(r);
    }
    return area == RectArea(t.covered);
}

inline bool ProjectCoveredPoint(const PointerTopology& t, uint64_t routing, POINT p, POINT& result) {
    if (!ValidPointerTopology(t, routing) || !RectContains(t.covered, p)) return false;
    POINT local{ p.x - t.covered.left, p.y - t.covered.top };
    for (size_t slot = 0; slot < t.slots.size(); ++slot) {
        if (!RectContains(t.slots[slot], local)) continue;
        const size_t source = (routing >> (slot * 4)) & 15;
        result = MapPoint(local, t.slots[slot], t.sources[source]);
        return true;
    }
    return false;
}

inline bool RoutePointer(const PointerTopology& t, uint64_t routing, POINT previous, POINT dest, POINT& result) {
    if (!ValidPointerTopology(t, routing)) return false;
    // Entering the covered panel from a real neighbour means entering the
    // visible split, not hitting a wall at the hidden desktop underneath it.
    if (PointOnAnyRect(t.others, previous) && RectContains(t.covered, dest))
        return ProjectCoveredPoint(t, routing, dest, result);

    for (size_t slot = 0; slot < t.slots.size(); ++slot) {
        const auto source = (routing >> (slot * 4)) & 15;
        const auto& r = t.sources[source];
        if (!RectContains(r, previous)) continue;
        const auto& local = t.slots[slot];
        POINT logical = MapPoint(dest, r, local);
        logical.x += t.covered.left; logical.y += t.covered.top;
        const bool stationary = previous.x == dest.x && previous.y == dest.y;
        POINT edge = logical;
        if (local.left == 0 && dest.x <= r.left && (dest.x < previous.x || stationary)) {
            edge.x = t.covered.left - 1;
            if (PointOnAnyRect(t.others, edge)) { result = edge; return true; }
        }
        if (local.right == t.covered.right - t.covered.left && dest.x >= r.right - 1 && (dest.x > previous.x || stationary)) {
            edge = logical; edge.x = t.covered.right;
            if (PointOnAnyRect(t.others, edge)) { result = edge; return true; }
        }
        if (local.top == 0 && dest.y <= r.top && (dest.y < previous.y || stationary)) {
            edge = logical; edge.y = t.covered.top - 1;
            if (PointOnAnyRect(t.others, edge)) { result = edge; return true; }
        }
        if (local.bottom == t.covered.bottom - t.covered.top && dest.y >= r.bottom - 1 && (dest.y > previous.y || stationary)) {
            edge = logical; edge.y = t.covered.bottom;
            if (PointOnAnyRect(t.others, edge)) { result = edge; return true; }
        }
        // The storage location of capture desktops is not the physical layout.
        if (PointOnAnyRect(t.others, dest)) {
            result = {std::clamp(dest.x, r.left, r.right - 1), std::clamp(dest.y, r.top, r.bottom - 1)};
            return true;
        }
    }
    if (PointOnAnyRect(t.others, previous) && PointOnAnyRect(t.sources, dest)) {
        for (const auto& r : t.others) if (RectContains(r, previous)) {
            result = {std::clamp(dest.x, r.left, r.right - 1), std::clamp(dest.y, r.top, r.bottom - 1)};
            return true;
        }
    }
    return false;
}
} // namespace ds
