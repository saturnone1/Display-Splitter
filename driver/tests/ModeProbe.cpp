// Pure timing/EDID checks. Never opens a driver or touches display configuration.
#include "../Timing.h"
#include "../Edid.h"
#include <cstdio>

int main()
{
    unsigned passed = 0;
    const DESKSPLIT_MODE modes[] = {
        {960, 540, 60000}, {1920, 540, 144000}, {480, 1080, 60000},
        {1, 1, 60000}, {320, 180, 60000}, {10000, 540, 60000},
        {16384, 16384, 60000}, {960, 540, 1}, {960, 540, 1200000},
        {1, 1, 0xFFFFFFFFu}
    };
    for (const auto& mode : modes) {
        DESKSPLIT_TIMING timing = {};
        if (!DeskSplitComputeTiming(mode.Width, mode.Height, mode.RefreshMillihertz, &timing)
            || timing.HActive != mode.Width || timing.VActive != mode.Height || !timing.PixelClockHz
            || !timing.VSyncDenominator || !timing.HSyncDenominator) return 1;
        DESKSPLIT_CONFIG config = {};
        config.Version = DESKSPLIT_PROTOCOL_VERSION;
        config.MonitorCount = DESKSPLIT_MAX_MONITORS;
        for (auto& monitor : config.Monitors) {
            monitor.ModeCount = 1;
            monitor.Modes[0] = mode;
        }
        if (!DeskSplitIsConfigValid(config)) return 2;
        for (UINT32 slot = 0; slot < DESKSPLIT_MAX_MONITORS; slot++) {
            BYTE edid[DESKSPLIT_EDID_SIZE] = {};
            if (!DeskSplitBuildEdid(slot, config.Monitors[slot], edid)) return 3;
            unsigned checksum = 0;
            for (BYTE value : edid) checksum += value;
            if ((checksum & 0xFFu) != 0) return 4;
            passed++;
        }
    }
    DESKSPLIT_TIMING timing = {};
    if (DeskSplitComputeTiming(0, 540, 60000, &timing)
        || DeskSplitComputeTiming(960, 0, 60000, &timing)
        || DeskSplitComputeTiming(960, 540, 0, &timing)
        || DeskSplitComputeTiming(16385, 540, 60000, &timing)
        || DeskSplitComputeTiming(16384, 16384, 0xFFFFFFFFu, &timing)) return 5;
    std::printf("PASS driver modes: %u EDID/timing cases, FHD 960x540, tiny/large modes, uncapped positive refresh and representation bounds; no driver/display calls\n", passed);
    return 0;
}
