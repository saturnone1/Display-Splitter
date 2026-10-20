using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace DesktopSplitter.Services;

public static class AppAppearance
{
    public static void Apply(bool? highContrast = null)
    {
        bool accessible = highContrast ?? SystemParameters.HighContrast;
        ApplicationThemeManager.Apply(accessible ? ApplicationTheme.HighContrast : ApplicationTheme.Dark, WindowBackdropType.None);
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Themes/Charcoal.xaml", StringComparison.Ordinal) == true);
        if (existing is not null) dictionaries.Remove(existing);
        if (!accessible) dictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DesktopSplitter;component/Themes/Charcoal.xaml") });
        foreach (Window window in System.Windows.Application.Current.Windows) ApplyWindow(window);
    }

    public static void AttachWindow(Window window)
        => window.SourceInitialized += (_, _) => ApplyWindow(window);

    private static void ApplyWindow(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        int dark = SystemParameters.HighContrast ? 0 : 1;
        // Supported by the minimum Windows 10 21H2 baseline; affects this HWND only.
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint handle, uint attribute, ref int value, int size);
}
