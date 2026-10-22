#include "WindowRescuer.h"
#include "MonitorUtil.h"
#include "Geometry.h"
#include "Log.h"

#include <dwmapi.h>
#include <algorithm>

namespace ds {

namespace {

// Only one rescuer exists; WinEvent callbacks carry no user data.
WindowRescuer* g_instance = nullptr;

constexpr DWORD kDebounceMs    = 300;   // LOCATIONCHANGE quiet period
constexpr DWORD kRescueEveryMs = 5000;  // per-hwnd rate limit
constexpr DWORD kRectsMaxAgeMs = 1000;  // lazy re-resolve of display geometry

// Shell surfaces, menus, tooltips and flyouts: never relocate these.
const wchar_t* const kExcludedClasses[] = {
    L"Progman",
    L"WorkerW",
    L"Shell_TrayWnd",
    L"Shell_SecondaryTrayWnd",
    L"NotifyIconOverflowWindow",
    L"TopLevelWindowForOverflowXamlIsland",
    L"Xaml_WindowedPopupClass",
    L"XamlExplorerHostIslandWindow",
    L"Windows.UI.Composition.DesktopWindowContentBridge",
    L"ForegroundStaging",
    L"MultitaskingViewFrame",
    L"TaskListThumbnailWnd",
    L"DV2ControlHost",
    L"SysShadow",
    L"#32768",              // menus
    L"tooltips_class32",
    L"DeskSplitCompositorWindow",
};

// Hosts whose windows are flyouts / logon UI rather than app windows.
const wchar_t* const kExcludedProcesses[] = {
    L"winlogon.exe",
    L"logonui.exe",
    L"shellexperiencehost.exe",
    L"startmenuexperiencehost.exe",
    L"searchhost.exe",
    L"textinputhost.exe",
};

bool IsExcludedClass(HWND hwnd) {
    wchar_t cls[128] = {};
    if (::GetClassNameW(hwnd, cls, _countof(cls)) == 0) return true;
    for (const wchar_t* name : kExcludedClasses) {
        if (::_wcsicmp(cls, name) == 0) return true;
    }
    return false;
}

bool IsExcludedProcess(HWND hwnd, DWORD ownPid) {
    DWORD pid = 0;
    ::GetWindowThreadProcessId(hwnd, &pid);
    if (pid == 0 || pid == ownPid) return true;

    HANDLE proc = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!proc) return true;   // protected / inaccessible: leave it alone

    wchar_t path[MAX_PATH] = {};
    DWORD   len = _countof(path);
    const BOOL ok = ::QueryFullProcessImageNameW(proc, 0, path, &len);
    ::CloseHandle(proc);
    if (!ok) return true;

    const wchar_t* base = ::wcsrchr(path, L'\\');
    base = base ? base + 1 : path;
    for (const wchar_t* name : kExcludedProcesses) {
        if (::_wcsicmp(base, name) == 0) return true;
    }
    return false;
}

bool IsCloaked(HWND hwnd) {
    DWORD cloaked = 0;
    const HRESULT hr = ::DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked,
                                               sizeof(cloaked));
    return SUCCEEDED(hr) && cloaked != 0;
}

} // namespace

WindowRescuer::~WindowRescuer() {
    Stop();
}

bool WindowRescuer::Start(const std::wstring& coveredDevice,
                          const std::vector<std::wstring>& targetDevices) {
    if (m_thread.joinable()) return true;

    m_coveredDevice = coveredDevice;
    m_targetDevices = targetDevices;
    m_ownPid = ::GetCurrentProcessId();
    m_ready = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_instance = this;

    m_thread = std::thread([this] { ThreadMain(); });

    if (m_ready) ::WaitForSingleObject(m_ready, 5000);
    return true;
}

void WindowRescuer::Stop() {
    const DWORD tid = m_threadId.load(std::memory_order_acquire);
    if (tid != 0) ::PostThreadMessageW(tid, WM_QUIT, 0, 0);
    if (m_thread.joinable()) m_thread.join();
    m_threadId.store(0, std::memory_order_release);
    if (m_ready) {
        ::CloseHandle(m_ready);
        m_ready = nullptr;
    }
    if (g_instance == this) g_instance = nullptr;
}

void WindowRescuer::OnDisplayChange() {
    m_rectsDirty.store(true, std::memory_order_release);
}

void CALLBACK WindowRescuer::EventProc(HWINEVENTHOOK, DWORD event, HWND hwnd,
                                       LONG idObject, LONG idChild, DWORD, DWORD) {
    if (g_instance) g_instance->HandleEvent(event, hwnd, idObject, idChild);
}

void WindowRescuer::ThreadMain() {
    ::SetThreadDescription(::GetCurrentThread(), L"dscomp-rescuer");
    m_threadId.store(::GetCurrentThreadId(), std::memory_order_release);

    // Force a message queue to exist before anyone can PostThreadMessage to us.
    MSG probe = {};
    ::PeekMessageW(&probe, nullptr, WM_USER, WM_USER, PM_NOREMOVE);

    const DWORD flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
    m_hookShow = ::SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, nullptr,
                                   EventProc, 0, 0, flags);
    m_hookMoveSizeEnd = ::SetWinEventHook(EVENT_SYSTEM_MOVESIZEEND,
                                          EVENT_SYSTEM_MOVESIZEEND, nullptr,
                                          EventProc, 0, 0, flags);
    m_hookLocation = ::SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE,
                                       EVENT_OBJECT_LOCATIONCHANGE, nullptr,
                                       EventProc, 0, 0, flags);
    m_hookForeground = ::SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                                         nullptr, EventProc, 0, 0, flags);

    const bool ok = m_hookShow || m_hookMoveSizeEnd || m_hookLocation || m_hookForeground;
    if (ok) {
        DS_LOG(L"window rescuer watching '%s', relocating across %zu split displays",
               m_coveredDevice.c_str(), m_targetDevices.size());
    } else {
        DS_WARN(L"SetWinEventHook failed: %s - window rescuer inactive",
                HrString(HRESULT_FROM_WIN32(::GetLastError())));
    }
    if (m_ready) ::SetEvent(m_ready);

    if (ok) {
        // Existing hidden windows produce neither SHOW nor LOCATIONCHANGE when
        // the compositor starts. Evaluate them once without stealing focus.
        ::EnumWindows([](HWND window, LPARAM self) -> BOOL {
            reinterpret_cast<WindowRescuer*>(self)->Evaluate(window); return TRUE;
        }, reinterpret_cast<LPARAM>(this));
        MSG msg = {};
        while (::GetMessageW(&msg, nullptr, 0, 0) > 0) {
            if (msg.message == WM_TIMER && m_debounceTimer != 0 &&
                msg.wParam == m_debounceTimer) {
                FlushPending();
                continue;
            }
            ::TranslateMessage(&msg);
            ::DispatchMessageW(&msg);
        }
    }

    if (m_debounceTimer) {
        ::KillTimer(nullptr, m_debounceTimer);
        m_debounceTimer = 0;
    }
    if (m_hookShow)        { ::UnhookWinEvent(m_hookShow);        m_hookShow = nullptr; }
    if (m_hookMoveSizeEnd) { ::UnhookWinEvent(m_hookMoveSizeEnd); m_hookMoveSizeEnd = nullptr; }
    if (m_hookLocation)    { ::UnhookWinEvent(m_hookLocation);    m_hookLocation = nullptr; }
    if (m_hookForeground)  { ::UnhookWinEvent(m_hookForeground);  m_hookForeground = nullptr; }
    m_pending.clear();
    m_lastRescue.clear();
    DS_VERB(L"window rescuer thread exiting");
}

void WindowRescuer::HandleEvent(DWORD event, HWND hwnd, LONG idObject, LONG idChild) {
    if (event == EVENT_SYSTEM_FOREGROUND && hwnd) {
        Evaluate(::GetAncestor(hwnd, GA_ROOT), true);
        return;
    }
    // Only whole top-level windows; ignoring caret/cursor/child accessibility
    // objects is what makes watching LOCATIONCHANGE affordable at all.
    if (hwnd == nullptr || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;

    if (event == EVENT_OBJECT_LOCATIONCHANGE) {
        // Debounce: coalesce the storm a drag or animation produces and act
        // once things settle. SetTimer with the same id restarts the interval.
        if (std::find(m_pending.begin(), m_pending.end(), hwnd) == m_pending.end()) {
            if (m_pending.size() < 64) m_pending.push_back(hwnd);
        }
        // Feeding the previous id back in resets that timer; passing 0 (or an
        // id that no longer exists) makes the system allocate a fresh one.
        m_debounceTimer = ::SetTimer(nullptr, m_debounceTimer, kDebounceMs, nullptr);
        return;
    }
    Evaluate(hwnd);
}

void WindowRescuer::FlushPending() {
    if (m_debounceTimer) {
        ::KillTimer(nullptr, m_debounceTimer);
        m_debounceTimer = 0;
    }
    std::vector<HWND> batch;
    batch.swap(m_pending);
    for (HWND h : batch) Evaluate(h);
}

bool WindowRescuer::RefreshRects() {
    const DWORD now = ::GetTickCount();
    if (m_rectsDirty.exchange(false, std::memory_order_acq_rel)) {
        m_rectsValid = false;
    } else if (m_rectsValid && (now - m_rectsTick) < kRectsMaxAgeMs) {
        return true;
    }

    std::vector<MonitorEntry> all;
    EnumerateMonitors(all);

    bool haveCovered = false;
    std::vector<RECT> targets;
    m_monitorRects.clear();
    for (const MonitorEntry& e : all) {
        m_monitorRects.push_back(e.rect);
        if (::_wcsicmp(e.device.c_str(), m_coveredDevice.c_str()) == 0) {
            m_coveredRect = e.rect;
            haveCovered = true;
        }
        if (std::any_of(m_targetDevices.begin(), m_targetDevices.end(), [&](const auto& name) {
                return ::_wcsicmp(e.device.c_str(), name.c_str()) == 0;
            })) {
            targets.push_back(e.work);
        }
    }
    m_rectsValid = haveCovered && targets.size() == m_targetDevices.size() && UnionOfRects(targets, m_targetWork) && !RectIsEmpty(m_coveredRect) &&
                   !RectIsEmpty(m_targetWork);
    m_rectsTick = now;
    return m_rectsValid;
}

bool WindowRescuer::RateLimit(HWND hwnd) {
    const DWORD now = ::GetTickCount();
    auto it = m_lastRescue.find(hwnd);
    if (it != m_lastRescue.end() && (now - it->second) < kRescueEveryMs) {
        return false;   // too soon; don't fight an app that repositions itself
    }
    if (m_lastRescue.size() > 256) {
        for (auto i = m_lastRescue.begin(); i != m_lastRescue.end();) {
            i = ((now - i->second) > (kRescueEveryMs * 4)) ? m_lastRescue.erase(i)
                                                           : std::next(i);
        }
    }
    m_lastRescue[hwnd] = now;
    return true;
}

// Ordered cheapest-first: EVENT_OBJECT_SHOW fires for every window on the
// system, and almost none of them are on the covered monitor, so the geometry
// test runs before the class lookup and long before OpenProcess.
bool WindowRescuer::ShouldRescue(HWND hwnd, RECT& windowRect, bool activated) const {
    if (!::IsWindow(hwnd) || !::IsWindowVisible(hwnd) || ::IsIconic(hwnd)) return false;

    const LONG_PTR style = ::GetWindowLongPtrW(hwnd, GWL_STYLE);
    const LONG_PTR ex = ::GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    if (style & WS_CHILD) return false;
    if (ex & WS_EX_TOOLWINDOW) return false;
    if (ex & WS_EX_NOACTIVATE) return false;

    if (!::GetWindowRect(hwnd, &windowRect)) return false;
    if (RectIsEmpty(windowRect)) return false;
    if (!NeedsWindowRescue(windowRect, m_coveredRect, m_monitorRects, activated)) return false;

    if (::GetAncestor(hwnd, GA_ROOT) != hwnd) return false;      // top-level only
    if (IsExcludedClass(hwnd)) return false;
    if (IsCloaked(hwnd)) return false;
    if (IsExcludedProcess(hwnd, m_ownPid)) return false;
    return true;
}

void WindowRescuer::Evaluate(HWND hwnd, bool activated) {
    if (!RefreshRects()) return;

    RECT wr = {};
    if (!ShouldRescue(hwnd, wr, activated)) return;
    if (!activated && !RateLimit(hwnd)) return;

    wchar_t title[128] = {};
    ::GetWindowTextW(hwnd, title, _countof(title));
    wchar_t cls[64] = {};
    ::GetClassNameW(hwnd, cls, _countof(cls));
    const RECT before = wr;

    WINDOWPLACEMENT placement{};
    placement.length = sizeof(placement);
    if (!::GetWindowPlacement(hwnd, &placement)) return;
    const bool maximized = placement.showCmd == SW_SHOWMAXIMIZED;
    // Read the restore size without synchronously unmaximizing a foreign app.
    if (maximized) wr = placement.rcNormalPosition;
    const RECT place = ComputeRescuePlacement(m_targetWork, wr);
    MONITORINFO monitor{}; monitor.cbSize = sizeof(monitor);
    if (!::GetMonitorInfoW(::MonitorFromRect(&place, MONITOR_DEFAULTTONEAREST), &monitor)) return;
    const LONG dx = monitor.rcWork.left - monitor.rcMonitor.left;
    const LONG dy = monitor.rcWork.top - monitor.rcMonitor.top;
    placement.rcNormalPosition = {place.left-dx, place.top-dy, place.right-dx, place.bottom-dy};
    placement.flags = WPF_ASYNCWINDOWPLACEMENT;
    placement.showCmd = maximized ? SW_SHOWMAXIMIZED : SW_SHOWNOACTIVATE;
    placement.ptMaxPosition = {-1,-1};
    if (!::SetWindowPlacement(hwnd, &placement)) {
        DS_VERB(L"rescue request for '%s' [%s] failed: %s", title, cls,
                HrString(HRESULT_FROM_WIN32(::GetLastError())));
        return;
    }
    const int x=place.left, y=place.top, w=place.right-place.left, h=place.bottom-place.top;
    DS_VERB(L"requested rescue of '%s' [%s] from (%ld,%ld)-(%ld,%ld) to (%d,%d) %dx%d%s", title,
            cls, before.left, before.top, before.right, before.bottom, x, y, w, h,
            maximized ? L" (re-maximized)" : L"");
}

} // namespace ds
