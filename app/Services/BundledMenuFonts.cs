using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopSplitter.Services;

/// <summary>Process-private copies for WinForms/GDI menu text. No system font installation.</summary>
public sealed class BundledMenuFonts : IDisposable
{
    private readonly PrivateFontCollection _fonts = new();
    private readonly List<nint> _buffers = new();
    private readonly List<nint> _registrations = new();
    public Font? Regular { get; private set; }
    public Font? Bold { get; private set; }
    private bool _disposed;

    public BundledMenuFonts()
    {
        try
        {
            foreach (string file in new[] { "NanumSquareRoundR.ttf", "NanumSquareRoundB.ttf" })
            {
                using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/DesktopSplitter;component/Assets/Fonts/" + file)).Stream;
                using var content = new MemoryStream(); stream.CopyTo(content);
                byte[] bytes = content.ToArray();
                nint buffer = Marshal.AllocHGlobal(bytes.Length);
                _buffers.Add(buffer);
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                uint count = 0;
                nint registration = AddFontMemResourceEx(buffer, (uint)bytes.Length, 0, ref count);
                if (registration == 0) throw new InvalidOperationException("Embedded menu font unavailable");
                _registrations.Add(registration);
                _fonts.AddMemoryFont(buffer, bytes.Length);
            }
            Regular = new Font(_fonts.Families.Single(f => f.GetName(1033) == "NanumSquareRound Regular"), 10f);
            Bold = new Font(_fonts.Families.Single(f => f.GetName(1033) == "NanumSquareRound Bold"), 10f);
        }
        catch { Dispose(); } // The OS menu font remains a readable fallback.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Regular?.Dispose(); Bold?.Dispose(); Regular = null; Bold = null;
        _fonts.Dispose();
        foreach (nint registration in _registrations) _ = RemoveFontMemResourceEx(registration);
        foreach (nint buffer in _buffers) Marshal.FreeHGlobal(buffer);
    }

    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern nint AddFontMemResourceEx(nint data, uint size, nint reserved, ref uint count);
    [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveFontMemResourceEx(nint handle);
}
