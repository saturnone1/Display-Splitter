#include "CursorConfine.h"
#include "MonitorUtil.h"
#include "PointerTopology.h"
#include "Log.h"
#include <atomic>

namespace ds {
namespace {
SRWLOCK g_lock = SRWLOCK_INIT;
PointerTopology g_topology;
std::function<uint64_t()> g_routing;
HHOOK g_hook = nullptr;
POINT g_previous{};
bool g_havePrevious = false;
POINT g_warp{};
bool g_pendingWarp = false;

bool Redirect(POINT point) {
    g_previous = point; g_havePrevious = true;
    g_warp = point; g_pendingWarp = true;
    if (!::SetCursorPos(point.x, point.y)) {
        g_pendingWarp = false; g_havePrevious = false; return false;
    }
    POINT actual{};
    if (::GetCursorPos(&actual) && (actual.x != point.x || actual.y != point.y)) {
        // Another application may own ClipCursor. Do not fight its clip or
        // swallow input on the assumption that the requested warp succeeded.
        g_previous = actual; g_pendingWarp = false; return false;
    }
    return true;
}

LRESULT CALLBACK MouseHookProc(int code, WPARAM message, LPARAM data) {
    if (code != HC_ACTION || message != WM_MOUSEMOVE || !data)
        return ::CallNextHookEx(g_hook, code, message, data);
    const auto* ms = reinterpret_cast<const MSLLHOOKSTRUCT*>(data);
    if (ms->flags & LLMHF_INJECTED) return ::CallNextHookEx(g_hook, code, message, data);
    const POINT dest = ms->pt;
    if (g_pendingWarp && dest.x == g_warp.x && dest.y == g_warp.y) {
        return ::CallNextHookEx(g_hook, code, message, data);
    }
    g_pendingWarp = false;
    // Reuse vector capacity: no recurring heap allocation on mouse movement.
    static PointerTopology topology;
    uint64_t routing = 0;
    ::AcquireSRWLockShared(&g_lock);
    topology = g_topology;
    if (g_routing) routing = g_routing();
    ::ReleaseSRWLockShared(&g_lock);
    if (!ValidPointerTopology(topology, routing)) {
        g_previous = dest; g_havePrevious = true;
        return ::CallNextHookEx(g_hook, code, message, data);
    }
    POINT previous = g_previous;
    if (!g_havePrevious) ::GetCursorPos(&previous);
    POINT corrected{};
    if (RoutePointer(topology, routing, previous, dest, corrected)) {
        if (corrected.x != dest.x || corrected.y != dest.y) {
            if (Redirect(corrected)) return 1;
        }
    } else if (!RectIsEmpty(topology.covered) && RectContains(topology.covered, dest)) {
        corrected = SlideAlongEdge(topology.covered, previous, dest);
        if (!PointOnAnyRect(topology.sources, corrected) && !PointOnAnyRect(topology.others, corrected)) {
            corrected = previous;
        }
        if (Redirect(corrected)) return 1;
    }
    g_previous = dest; g_havePrevious = true;
    return ::CallNextHookEx(g_hook, code, message, data);
}
} // namespace

CursorConfiner::~CursorConfiner() { Release(); }

bool CursorConfiner::Configure(const AppConfig& config, std::function<uint64_t()> routing) {
    m_config = config; m_routing = std::move(routing); m_configured = true;
    Refresh(); return true;
}

void CursorConfiner::Refresh() {
    if (!m_configured) return;
    std::vector<MonitorEntry> monitors;
    EnumerateMonitors(monitors);
    PointerTopology topology;
    topology.sources.resize(m_config.segments.size());
    for (const auto& segment : m_config.segments) {
        const auto& r = segment.physRect;
        topology.slots.push_back({r.x, r.y, r.x + r.w, r.y + r.h});
    }
    for (const auto& monitor : monitors) {
        if (!_wcsicmp(monitor.device.c_str(), m_config.physicalDevice.c_str())) {
            topology.covered = monitor.rect; continue;
        }
        auto it = std::find_if(m_config.segments.begin(), m_config.segments.end(), [&](const auto& segment) {
            return !_wcsicmp(monitor.device.c_str(), segment.virtualDevice.c_str());
        });
        if (it == m_config.segments.end()) topology.others.push_back(monitor.rect);
        else topology.sources[static_cast<size_t>(it - m_config.segments.begin())] = monitor.rect;
    }
    const uint64_t routing = m_routing ? m_routing() : 0;
    ::AcquireSRWLockExclusive(&g_lock);
    g_topology = topology; g_routing = m_routing;
    ::ReleaseSRWLockExclusive(&g_lock);
    // No ClipCursor: a single rectangle cannot describe a multi-monitor layout
    // or its logical crossings, and must not override another application's clip.
    POINT current{};
    if (::GetCursorPos(&current)) {
        POINT mapped{};
        if (ProjectCoveredPoint(topology, routing, current, mapped)) Redirect(mapped);
        else { g_previous = current; g_havePrevious = true; }
    }
    DS_LOG(L"pointer topology: %zu split sources, %zu other displays; mapping %s",
        topology.sources.size(), topology.others.size(), ValidPointerTopology(topology, routing) ? L"ready" : L"unavailable");
}

void CursorConfiner::Reapply() { /* No global rectangular cursor clip. */ }

bool CursorConfiner::InstallHook() {
    if (g_hook) return true;
    g_hook = ::SetWindowsHookExW(WH_MOUSE_LL, MouseHookProc, ::GetModuleHandleW(nullptr), 0);
    if (!g_hook) DS_WARN(L"mouse routing hook unavailable: %s", HrString(HRESULT_FROM_WIN32(::GetLastError())));
    return g_hook != nullptr;
}

bool CursorConfiner::HookInstalled() const { return g_hook != nullptr; }

void CursorConfiner::Release() {
    if (g_hook) { ::UnhookWindowsHookEx(g_hook); g_hook = nullptr; }
    ::AcquireSRWLockExclusive(&g_lock);
    g_topology = {}; g_routing = {};
    ::ReleaseSRWLockExclusive(&g_lock);
    g_havePrevious = false; g_pendingWarp = false; m_configured = false;
}
} // namespace ds
