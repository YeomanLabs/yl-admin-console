using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace YeomanLabs.AdminConsole.App.Services;

/// <summary>One toast (UX §10): text, optional Undo, 8 s auto-dismiss paused while hovered.</summary>
public sealed partial class ToastItem : ObservableObject
{
    private readonly Func<Task>? _undo;
    private readonly Action<ToastItem> _dismiss;

    public ToastItem(string text, string? undoLabel, Func<Task>? undo, Action<ToastItem> dismiss)
    {
        Text = text;
        UndoLabel = undoLabel;
        _undo = undo;
        _dismiss = dismiss;
    }

    public string Text { get; }
    public string? UndoLabel { get; }
    public bool HasUndo => _undo is not null;

    /// <summary>Bound two-way by <see cref="Behaviors.HoverBehavior"/>; the dismiss timer waits while true.</summary>
    [ObservableProperty]
    private bool _isHovered;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _isUndoing;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_undo is null) return;
        IsUndoing = true;
        try
        {
            await _undo();
        }
        finally
        {
            _dismiss(this);
        }
    }

    private bool CanUndo() => HasUndo && !IsUndoing;

    [RelayCommand]
    private void Dismiss() => _dismiss(this);
}
