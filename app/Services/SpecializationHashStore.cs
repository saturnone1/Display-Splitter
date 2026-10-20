using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>
/// Remembers the 64-bit monitor identity hash that Windows passes to
/// <c>SystemSettingsAdminFlows.exe SpecializeDisplay</c>, per monitor.
///
/// The derivation is not known, so instead of computing the hash we OBSERVE it: the first time
/// the user removes a display from the desktop through Windows Settings, the app reads the
/// helper's command line and remembers the value. From then on the same display can be hidden and
/// restored in one click.
///
/// Safety rule, enforced here rather than left to callers: a hash is bound to the exact monitor
/// identity it was captured from. There is no fallback, no guessing and no cross-monitor reuse —
/// a wrong hash risks putting a display into a specialized state the Settings UI cannot undo.
/// </summary>
public static class SpecializationHashStore
{
    /// <summary>The identity a hash is keyed by. Empty when the monitor has no usable identity.</summary>
    public static string KeyFor(MonitorInfo monitor) => monitor.MonitorDevicePath ?? string.Empty;

    /// <summary>The learned hash for this exact monitor, or null.</summary>
    public static ulong? TryGet(MonitorInfo monitor)
    {
        string key = KeyFor(monitor);
        if (string.IsNullOrEmpty(key)) return null;

        AppSettings? settings = SettingsStore.Load();
        if (settings is not null)
        {
            foreach (KeyValuePair<string, string> entry in settings.SpecializationHashes)
            {
                if (!string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (ulong.TryParse(entry.Value, out ulong stored) && stored != 0) return stored;
            }
        }

        return null;
    }

    public static bool HasHash(MonitorInfo monitor) => TryGet(monitor) is not null;

    /// <summary>Records a hash observed for this exact monitor. Refuses zero.</summary>
    public static bool Learn(MonitorInfo monitor, ulong hash, IProgress<string>? log = null)
    {
        string key = KeyFor(monitor);
        if (string.IsNullOrEmpty(key) || hash == 0) return false;

        try
        {
            SettingsStore.Update(settings => {
                foreach (var existing in settings.SpecializationHashes.Keys.Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToArray())
                    settings.SpecializationHashes.Remove(existing);
                settings.SpecializationHashes[key] = hash.ToString();
            });

            log?.Report($"Learned the identity hash for {monitor.DisplayLabel}: {hash}. " +
                        "Display hiding can now be turned on and off automatically for this monitor.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Report("Could not save the learned identity hash: " + ex.Message);
            return false;
        }
    }

}
