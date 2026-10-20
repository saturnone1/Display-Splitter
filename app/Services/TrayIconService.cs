using System.Drawing;
using System.Windows.Forms;

namespace DesktopSplitter.Services;

/// <summary>
/// Tray presence backed by WinForms <see cref="NotifyIcon"/> (UseWindowsForms is enabled
/// alongside UseWPF purely for this) — no third-party tray package.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _applyLastItem;
    private readonly ToolStripMenuItem _revertItem;
    private Icon? _generatedIcon;
    private readonly BundledMenuFonts _fonts = new();
    private bool _disposed;

    public TrayIconService()
    {
        _applyLastItem = new ToolStripMenuItem("마지막 분할 적용");
        _applyLastItem.Click += (_, _) => ApplyLastRequested?.Invoke(this, EventArgs.Empty);

        _revertItem = new ToolStripMenuItem("원래 화면으로 복원");
        _revertItem.Click += (_, _) => RevertRequested?.Invoke(this, EventArgs.Empty);

        var openItem = new ToolStripMenuItem("DesktopSplitter 열기");
        if (_fonts.Bold is not null) openItem.Font = _fonts.Bold;
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("종료");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu = new ContextMenuStrip();
        if (_fonts.Regular is not null) _menu.Font = _fonts.Regular;
        if (!SystemInformation.HighContrast) {
            _menu.BackColor = Color.FromArgb(41,44,48);
            _menu.ForeColor = Color.FromArgb(217,220,225);
            _menu.Renderer = new ToolStripProfessionalRenderer(new CharcoalMenuColors());
        }
        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_applyLastItem);
        _menu.Items.Add(_revertItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "DesktopSplitter",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ApplyLastRequested;
    public event EventHandler? RevertRequested;
    public event EventHandler? ExitRequested;

    public bool ApplyLastEnabled
    {
        get => _applyLastItem.Enabled;
        set => _applyLastItem.Enabled = value;
    }

    public bool RevertEnabled
    {
        get => _revertItem.Enabled;
        set => _revertItem.Enabled = value;
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed) return;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3000);
    }

    /// <summary>The same multi-resolution asset is used by the executable and WPF windows.</summary>
    private Icon CreateIcon()
    {
        try
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/DesktopSplitter;component/Assets/App.ico")).Stream;
            using var icon = new Icon(stream, 32, 32);
            _generatedIcon = (Icon)icon.Clone();
            return _generatedIcon;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _fonts.Dispose();
        _generatedIcon?.Dispose();
    }

    private sealed class CharcoalMenuColors : ProfessionalColorTable
    {
        private static Color Surface => Color.FromArgb(41,44,48);
        private static Color Border => Color.FromArgb(72,77,84);
        private static Color Selected => Color.FromArgb(60,66,73);
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Selected;
        public override Color MenuItemSelectedGradientBegin => Selected;
        public override Color MenuItemSelectedGradientEnd => Selected;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
    }
}
