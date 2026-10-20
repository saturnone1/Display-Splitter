using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public static class MonitorSelection
{
    public static MonitorInfo? Find(IReadOnlyList<MonitorInfo> monitors, string? path, string? device, string? legacyName = null)
    {
        // A known physical identity must never fall back to a recycled DISPLAYn/name.
        if (!string.IsNullOrWhiteSpace(path))
            return Unique(monitors.Where(m => string.Equals(m.MonitorDevicePath, path, StringComparison.OrdinalIgnoreCase)));
        var byDevice = Unique(monitors.Where(m => !string.IsNullOrWhiteSpace(device)
            && string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase)));
        return byDevice ?? Unique(monitors.Where(m => !string.IsNullOrWhiteSpace(legacyName)
            && string.Equals(m.FriendlyName, legacyName, StringComparison.OrdinalIgnoreCase)));
    }

    private static MonitorInfo? Unique(IEnumerable<MonitorInfo> matches)
    {
        var candidates = matches.Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
