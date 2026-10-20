// DisplayTaskPool2 is UniversalApiContract v13 (Windows 11). Windows 10 21H2
// exposes the original task pool, so never call the newer interface unconditionally.
#pragma once

#pragma warning(push, 0)
#include <winrt/Windows.Devices.Display.Core.h>
#pragma warning(pop)
#include <optional>

namespace ds {

inline std::optional<winrt::Windows::Devices::Display::Core::DisplayTaskResult>
SubmitDisplayTask(
    const winrt::Windows::Devices::Display::Core::IDisplayTaskPool& legacyPool,
    const winrt::Windows::Devices::Display::Core::IDisplayTaskPool2& modernPool,
    const winrt::Windows::Devices::Display::Core::DisplayTask& task) {
    if (modernPool) return modernPool.TryExecuteTask(task);

    // This is the supported API on Windows 10. Exceptions must reach the caller;
    // do not resubmit through the other interface after a failed submission.
#pragma warning(push)
#pragma warning(disable: 4996)
    legacyPool.ExecuteTask(task);
#pragma warning(pop)
    return std::nullopt; // legacy submission provides no structured present status
}

} // namespace ds
