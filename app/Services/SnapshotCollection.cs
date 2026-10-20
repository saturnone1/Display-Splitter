using System.Collections.ObjectModel;

namespace DesktopSplitter.Services;

/// <summary>Keep equal rows and their WPF containers; never reset a whole bound collection.</summary>
public static class SnapshotCollection
{
    public static void Update<T>(ObservableCollection<T> current, IReadOnlyList<T> snapshot)
    {
        for (int i = 0; i < snapshot.Count; i++)
            if (i >= current.Count) current.Add(snapshot[i]);
            else if (!EqualityComparer<T>.Default.Equals(current[i], snapshot[i])) current[i] = snapshot[i];
        while (current.Count > snapshot.Count) current.RemoveAt(current.Count - 1);
    }
}
