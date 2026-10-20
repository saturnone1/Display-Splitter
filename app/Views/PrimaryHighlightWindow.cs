using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopSplitter.Services;

namespace DesktopSplitter.Views;

/// <summary>Visual feedback only. Never activates, intercepts clicks, or places other windows.</summary>
internal sealed class PrimaryHighlightWindow : Window
{
    // A cosmetic animation duration, independent of display safety/recovery deadlines.
    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromSeconds(1.2);
    private readonly WindowBox _bounds;

    internal PrimaryHighlightWindow(WindowBox bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        _bounds = bounds;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        IsHitTestVisible = false;
        Width = Height = 1;
        Opacity = 0;
        Content = new Border {
            BorderBrush = new SolidColorBrush(Color.FromRgb(100, 203, 143)),
            BorderThickness = new Thickness(3), Background = Brushes.Transparent
        };
        SourceInitialized += (_, _) => {
            var handle = new WindowInteropHelper(this).Handle;
            // Layered transparency passes mouse input through; NOACTIVATE also protects focus.
            const int extendedStyle = -20;
            const long passiveStyles = 0x08000000 | 0x80 | 0x20;
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLongPtr(handle, extendedStyle, GetWindowLongPtr(handle, extendedStyle) | (nint)passiveStyles) == 0
                && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!SetWindowPos(handle, new nint(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x0010))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        };
        Loaded += (_, _) => {
            var fade = new DoubleAnimationUsingKeyFrames { Duration = new Duration(FeedbackDuration) };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.65, KeyTime.FromPercent(0.15)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.65, KeyTime.FromPercent(0.45)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        BeginAnimation(OpacityProperty, null);
        base.OnClosed(e);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
