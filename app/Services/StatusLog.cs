using System.Text;

namespace DesktopSplitter.Services;

/// <summary>Bounded display history. Full lines can independently be forwarded to a file sink.</summary>
public sealed class StatusLog(int maxCharacters = 64 * 1024)
{
    private readonly Queue<string> _lines = new();
    private int _length;
    public void Append(string line)
    {
        if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        if (line.Length > maxCharacters) line = line[^maxCharacters..];
        while (_lines.Count > 0 && _length + Environment.NewLine.Length + line.Length > maxCharacters)
        {
            _length -= _lines.Dequeue().Length;
            if (_lines.Count > 0) _length -= Environment.NewLine.Length;
        }
        if (_lines.Count > 0) _length += Environment.NewLine.Length;
        _lines.Enqueue(line);
        _length += line.Length;
    }
    public override string ToString() => string.Join(Environment.NewLine, _lines);
    public int Length => _length;
}
