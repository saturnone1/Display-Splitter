using System.Windows.Threading;

namespace DesktopSplitter.Services;

/// <summary>Native window hooks and WinForms callbacks need an explicit WPF async context,
/// even when they arrive on the same OS thread as the dispatcher.</summary>
public static class DesktopUiThread
{
    public static Task RunAsync(Dispatcher? dispatcher, Func<Task> action)
        => dispatcher is null || (dispatcher.CheckAccess() && SynchronizationContext.Current is DispatcherSynchronizationContext)
            ? action() : dispatcher.InvokeAsync(action).Task.Unwrap();
}
