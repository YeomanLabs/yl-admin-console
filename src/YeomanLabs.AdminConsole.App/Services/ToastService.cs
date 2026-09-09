using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace YeomanLabs.AdminConsole.App.Services;

/// <summary>
/// UX §10 toasts: bottom-right, at most three stacked, 8 s auto-dismiss that pauses while the pointer is over the toast.
/// Success toasts for every write ("Removed Visio Plan 2 from Jane Doe · Undo"); Undo re-issues the inverse and toasts again.
/// </summary>
public sealed class ToastService
{
    private const int MaxVisible = 3;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);

    private readonly ILogger<ToastService> _logger;

    public ToastService(ILogger<ToastService> logger) => _logger = logger;

    public ObservableCollection<ToastItem> Items { get; } = [];

    /// <summary>Safe from any thread.</summary>
    public void Show(string text, string? undoLabel = null, Func<Task>? undo = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(text, undoLabel, undo));
            return;
        }

        var toast = new ToastItem(text, undoLabel, undo, Remove);
        while (Items.Count >= MaxVisible) Items.RemoveAt(0);
        Items.Add(toast);
        _logger.LogDebug("Toast: {Text}", text);
        _ = ExpireAsync(toast);
    }

    private async Task ExpireAsync(ToastItem toast)
    {
        var remaining = Lifetime;
        var tick = TimeSpan.FromMilliseconds(250);
        while (remaining > TimeSpan.Zero && Items.Contains(toast))
        {
            await Task.Delay(tick);
            if (!toast.IsHovered && !toast.IsUndoing) remaining -= tick;
        }

        Remove(toast);
    }

    private void Remove(ToastItem toast)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Remove(toast));
            return;
        }

        Items.Remove(toast);
    }
}
