using System.IO;
using System.Text;

namespace DesktopSplitter.Services;

/// <summary>Commit complete UTF-8 content in the destination directory; retain the old file on failure.</summary>
public static class AtomicFile
{
    public static void WriteText(string path, string content)
    {
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string pending = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(pending, path, destinationBackupFileName: null);
            else File.Move(pending, path);
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
