using System.IO;
using System.Text.Json;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>Reads/writes %APPDATA%\DesktopSplitter\settings.json (last applied UI state).</summary>
public static class SettingsStore
{
    private static readonly SettingsFile Store = new(FilePath);

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopSplitter");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings? Load()
        => Store.Load();

    public static void Save(AppSettings settings)
        => Store.Save(settings);

    public static void Update(Action<AppSettings> update)
        => Store.Update(update);
}
