using DesktopSplitter.Models;
using DesktopSplitter.Interop;

namespace DesktopSplitter.Services;

public sealed class ApplyException : Exception
{
    public ApplyException(string message) : base(message) { }
    public ApplyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The dialogs the display-hiding sequence needs. Every method is called from a BACKGROUND
/// thread and blocks until the user answers, so implementations must marshal to the UI thread.
/// </summary>
public interface ISpecializationUi
{
    /// <summary>
    /// The programmatic DisplayConfig SET was refused. Walk the user through doing the same thing
    /// in Windows Settings ("Remove display from desktop"), polling until it takes effect.
    /// Returns true once the monitor really is off the desktop.
    /// </summary>
    bool GuideManualEnable(MonitorInfo physical, SpecializationResult failure, CancellationToken ct);

    /// <summary>
    /// Keep-or-revert countdown, shown once the compositor confirms it is presenting through the
    /// hidden display. Returns true to keep. MUST resolve to false on its own timeout — the app
    /// is the auto-revert authority.
    /// </summary>
    bool ConfirmKeep(MonitorInfo physical, int timeoutSeconds, CancellationToken ct);

    /// <summary>
    /// Undoing the hide programmatically was refused too. Tell the user how to put the monitor
    /// back themselves, and wait until they have (or they dismiss).
    /// </summary>
    void GuideManualDisable(MonitorInfo physical, SpecializationResult failure, CancellationToken ct);
}

/// <summary>
/// The "Keep these display settings?" countdown, mirroring what Windows shows after a resolution
/// change. Called from a BACKGROUND thread and blocks; returns true to keep.
/// MUST resolve to false when the countdown lapses — that timeout is the safety net for a change
/// that left the user unable to see or click anything.
/// </summary>
public interface IApplyConfirmer
{
    bool ConfirmApply(string summary, int timeoutSeconds, CancellationToken ct);
}

/// <summary>
/// The configuration that was live before an apply, so a declined change can be put back exactly.
/// </summary>
public sealed record AppliedSnapshot(
    bool WasApplied,
    string PhysicalDevice,
    LayoutKind Layout,
    IReadOnlyList<(int Width, int Height)> Resolutions,
    bool HidePhysicalDisplay)
{
    public int PrimarySlot { get; init; } = 1; // Matrix representative only, never Windows primary selection.
    public IReadOnlyList<LayoutCut> CustomCuts { get; init; } = Array.Empty<LayoutCut>();
    public IReadOnlyList<int>? RoutingOrder { get; init; }
    public string PhysicalMonitorPath { get; init; } = "";
    public static readonly AppliedSnapshot None =
        new(false, string.Empty, LayoutKind.TwoVertical, Array.Empty<(int, int)>(), false);
}

public sealed class ApplyRequest
{
    public required MonitorInfo Physical { get; init; }
    public required LayoutDefinition Layout { get; init; }

    /// <summary>One entry per layout segment, in layout order.</summary>
    public required IReadOnlyList<(int Width, int Height)> Resolutions { get; init; }
    public int PrimarySlot { get; init; } = 1; // Matrix representative only, never Windows primary selection.

    /// <summary>Try to remove the physical monitor from the desktop once the split is up.</summary>
    public bool HidePhysicalDisplay { get; init; }

    /// <summary>Supplies the hiding dialogs. Without one, hiding is kept automatically.</summary>
    public ISpecializationUi? SpecializationUi { get; init; }

    /// <summary>
    /// Show the keep-or-revert countdown after applying. Set ONLY for user-initiated applies:
    /// startup auto-apply, crash recovery and rollback re-applies must never prompt.
    /// </summary>
    public bool PromptToConfirm { get; init; }

    /// <summary>Required when PromptToConfirm is true; missing confirmation fails preflight.</summary>
    public IApplyConfirmer? Confirmer { get; init; }
    public Action? OnTopologyReady { get; init; }
    public Action? OnRolledBack { get; init; }
    public int ConfirmationSeconds { get; init; } = AppSettings.DefaultSafetyConfirmationSeconds;
    public IProgress<DisplayOperationState>? State { get; init; }
}

public sealed class ApplyResult
{
    public required IReadOnlyList<MonitorInfo> VirtualMonitors { get; init; }
    public required string ConfigPath { get; init; }
    public required int CompositorPid { get; init; }

    /// <summary>True when the user declined (or ignored) the countdown and we put things back.</summary>
    public bool RolledBack { get; init; }

    /// <summary>What we rolled back to, when <see cref="RolledBack"/> is set.</summary>
    public string RollbackDescription { get; init; } = string.Empty;
}
