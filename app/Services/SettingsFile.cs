using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>Atomic JSON storage with a per-path mutex shared by application instances.</summary>
public sealed class SettingsFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _mutexName;

    public SettingsFile(string path)
    {
        _path = Path.GetFullPath(path);
        _mutexName = @"Local\DesktopSplitter.Settings." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())));
    }

    public AppSettings? Load() => Locked(() => LoadCore(tolerateErrors: true));
    public void Save(AppSettings settings) => Locked(() => { SaveCore(settings); return true; });
    public void Update(Action<AppSettings> update) => Locked(() => {
        var settings = LoadCore(tolerateErrors: false) ?? new AppSettings();
        update(settings);
        SaveCore(settings);
        return true;
    });

    private void SaveCore(AppSettings settings) => AtomicFile.WriteText(_path, JsonSerializer.Serialize(settings, Options));
    private AppSettings? LoadCore(bool tolerateErrors)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var text = new StreamReader(input);
            var settings = JsonSerializer.Deserialize<AppSettings>(text.ReadToEnd(), Options);
            if (settings is null) return null;
            settings.Segments ??= new();
            settings.CustomLayoutCuts ??= new();
            settings.Shortcuts ??= new();
            settings.SpecializationHashes ??= new();
            settings.PhysicalDevice ??= "";
            settings.PhysicalMonitorPath ??= "";
            settings.MatrixMemories ??= new();
            settings.MatrixPrimarySlots ??= new();
            settings.MatrixMemories.RemoveAll(snapshot => snapshot is null);
            foreach (var matrix in settings.MatrixMemories) matrix.Windows ??= new();
            settings.PrimarySegmentSlot = Math.Clamp(settings.PrimarySegmentSlot, 1, 9);
            settings.PhysicalFriendlyName ??= "";
            settings.PreviousPrimaryDevice ??= "";
            settings.PreviousPrimaryFriendlyName ??= "";
            settings.AppliedPrimaryDevice ??= "";
            settings.SpecializedDevice ??= "";
            settings.SpecializedDevicePath ??= "";
            settings.SpecializationHashRecipe ??= "";
            return settings;
        }
        catch (Exception error) when (tolerateErrors && (error is IOException or UnauthorizedAccessException or JsonException))
        {
            return null; // A corrupt/unavailable settings file cannot prevent opening the editor.
        }
    }

    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        bool owned = false;
        try
        {
            try { owned = mutex.WaitOne(); }
            catch (AbandonedMutexException) { owned = true; }
            return action();
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }
}
