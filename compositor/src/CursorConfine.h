// CursorConfine.h - keeps the OS cursor off the covered physical monitor.
//
// Maps the covered physical panel's logical edges to split capture desktops.
// Other physical displays retain their original adjacency. No global cursor clip.
#pragma once

#include "Common.h"
#include <functional>

namespace ds {

class CursorConfiner {
public:
    CursorConfiner() = default;
    ~CursorConfiner();

    CursorConfiner(const CursorConfiner&) = delete;
    CursorConfiner& operator=(const CursorConfiner&) = delete;

    // Resolve capture sources and visible slots; routing is read atomically on each move.
    bool Configure(const AppConfig& config, std::function<uint64_t()> routing);

    // Re-resolves monitor rectangles after topology or slot-order changes.
    // Must be called on WM_DISPLAYCHANGE: making a virtual monitor primary
    // re-bases virtual-desktop coordinates, so every cached rect moves.
    void Refresh();

    // Compatibility no-op: never reassert a global clip.
    void Reapply();

    // Installs the WH_MOUSE_LL hook. Must be called on a thread that pumps
    // messages; the hook procedure runs on that thread. Returns false if the
    // hook could not be installed - the caller carries on unconfined.
    bool InstallHook();

    bool HookInstalled() const;

    // Unhooks and forgets pointer routing state.
    void Release();

private:
    AppConfig m_config;
    std::function<uint64_t()> m_routing;
    bool         m_configured = false;
};

} // namespace ds
