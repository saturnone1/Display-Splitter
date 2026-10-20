using System.Text;
using System.IO;
using System.Threading.Channels;

namespace DesktopSplitter.Services;

/// <summary>Non-blocking session diagnostics with bounded queue/files and explicit loss reporting.</summary>
public sealed class FileStatusLog : IAsyncDisposable
{
    private readonly Channel<string> _queue;
    private readonly Task _writer;
    private long _dropped;
    public FileStatusLog(string directory, int queueCapacity = 1024, int maxFileBytes = 4 * 1024 * 1024, int maxFiles = 16)
    {
        if (queueCapacity <= 0 || maxFileBytes < 256 || maxFiles <= 0) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(queueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _writer = Task.Run(async () => {
            StreamWriter? output = null;
            try {
                Directory.CreateDirectory(directory);
                string session = $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}";
                int part = 0, bytes = 0;
                async Task WriteAsync(string line) {
                    int limit = (maxFileBytes - 64) / 3;
                    if (line.Length > limit) line = line[..limit] + " [diagnostic truncated]";
                    int size = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                    if (output is null || bytes + size > maxFileBytes) {
                        if (output is not null) await output.DisposeAsync();
                        string activePath = Path.GetFullPath(Path.Combine(directory, $"{session}-{part++:D6}.log"));
                        output = new StreamWriter(activePath, false, new UTF8Encoding(false));
                        bytes = 0;
                        // Closing a buffered file can update its timestamp after the next one is created.
                        // Protect the active file explicitly; ties must not evict the wrong segment.
                        var oldFiles = new DirectoryInfo(directory).GetFiles("session-*.log")
                            .Where(file => !string.Equals(file.FullName, activePath, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(file => file.CreationTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal).ToArray();
                        foreach (var old in oldFiles.Skip(maxFiles - 1))
                            try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                    await output.WriteLineAsync(line);
                    bytes += size;
                }
                await foreach (string line in _queue.Reader.ReadAllAsync()) {
                    long lost = Interlocked.Exchange(ref _dropped, 0);
                    if (lost > 0) await WriteAsync($"[diagnostic overflow] {lost} lines omitted");
                    await WriteAsync(line);
                    if (!_queue.Reader.TryPeek(out _) && output is not null) await output.FlushAsync();
                }
                long finalLost = Interlocked.Exchange(ref _dropped, 0);
                if (finalLost > 0) await WriteAsync($"[diagnostic overflow] {finalLost} lines omitted");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                Failure = error.Message;
                _queue.Writer.TryComplete();
            }
            finally { if (output is not null) try { await output.DisposeAsync(); } catch (IOException error) { Failure = error.Message; } }
        });
    }
    public string? Failure { get; private set; }
    public void Append(string line) { if (!_queue.Writer.TryWrite(line)) Interlocked.Increment(ref _dropped); }
    public async ValueTask DisposeAsync() { _queue.Writer.TryComplete(); await _writer.ConfigureAwait(false); }
}
