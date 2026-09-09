using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace YeomanLabs.AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// One modal dialog. The caller awaits <see cref="Completion"/>: true when the admin confirmed, false when they
/// cancelled, pressed Esc, or clicked the backdrop. Every destructive action in UX §4 and §5 goes through one of these.
/// </summary>
public abstract partial class DialogViewModelBase : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> Completion => _result.Task;

    public abstract string Title { get; }

    /// <summary>Body text under the title. Bullets carry the per-item detail (a preview list, what a wipe removes).</summary>
    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<string> Bullets { get; init; } = [];

    public virtual string ConfirmText => "OK";

    public virtual string CancelText => "Cancel";

    /// <summary>Paints the confirm button red (UX §2: destructive actions are never the quiet default).</summary>
    public virtual bool IsDestructive => false;

    /// <summary>Guards the confirm button — e.g. the typed device name has to match before a wipe can run.</summary>
    public virtual bool CanConfirm => true;

    public virtual bool ShowCancel => true;

    /// <summary>
    /// False when the dialog draws its own buttons. The offboard dialog does: its footer changes with the phase, and
    /// "Run 11 steps" must not close the dialog the way a confirm does.
    /// </summary>
    public virtual bool ShowFooter => true;

    /// <summary>Dialog width in pixels. A step checklist needs more room than a confirm.</summary>
    public virtual double Width => 520;

    public bool HasMessage => Message.Length > 0;

    public bool HasBullets => Bullets.Count > 0;

    [RelayCommand]
    private void Confirm()
    {
        if (CanConfirm) _result.TrySetResult(true);
    }

    /// <summary>Cancel, Esc, and the backdrop all land here. Safe to call twice.</summary>
    [RelayCommand]
    private void Cancel() => _result.TrySetResult(false);
}
