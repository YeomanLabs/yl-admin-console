using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Writes;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One editable row of the Identity property sheet (UX §4 tab 1): edit-on-blur with an inline spinner then a check,
/// no Save button. Read-only rows carry the hybrid caption instead (UX §6). Arrays are edited as a comma-separated list.
/// </summary>
public sealed partial class EditableField : ObservableObject
{
    private readonly Func<string, object?, Task<WriteOutcome>> _save;
    private string _committed;

    public EditableField(string label, string propertyName, string? value, Func<string, object?, Task<WriteOutcome>> save,
        bool isMono = false, bool isArray = false, bool isReadOnly = false, string? readOnlyReason = null, string? placeholder = null)
    {
        Label = label;
        PropertyName = propertyName;
        IsMono = isMono;
        IsArray = isArray;
        _isReadOnly = isReadOnly;
        _readOnlyReason = readOnlyReason;
        Placeholder = placeholder;
        _save = save;
        _committed = value ?? string.Empty;
        _value = _committed;
    }

    public string Label { get; }
    public string PropertyName { get; }
    public bool IsMono { get; }
    public bool IsArray { get; }
    public string? Placeholder { get; }

    /// <summary>
    /// Mutable because a hybrid field's fate is not known when the row is built (M12.4): the account is synced, so
    /// the field starts locked, and it unlocks a moment later if a domain controller turns out to be reachable and
    /// the AD object behind this user is found. Rebuilding the rows at that point would throw away half-typed text.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    private bool _isReadOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadOnlyReason))]
    [NotifyPropertyChangedFor(nameof(ShowReadOnlyReason))]
    private string? _readOnlyReason;

    public bool IsEditable => !IsReadOnly;
    public bool HasReadOnlyReason => !string.IsNullOrEmpty(ReadOnlyReason);

    /// <summary>True once this field writes to on-prem AD rather than to Graph (M12.4).</summary>
    [ObservableProperty]
    private bool _writesToOnPrem;

    /// <summary>
    /// Set after a write that landed in AD but has not reached the cloud yet. It clears itself the moment Graph
    /// reports the new value, which is how the admin stops wondering whether the sync has run (doc 01).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAwaitingSync))]
    [NotifyPropertyChangedFor(nameof(ShowPendingSync))]
    [NotifyPropertyChangedFor(nameof(ShowReadOnlyReason))]
    private string? _pendingSyncText;

    public bool IsAwaitingSync => !string.IsNullOrEmpty(PendingSyncText);

    // One caption slot, three things that could fill it. An error is the most urgent, then the fact that a value is
    // waiting on a sync, and only then the standing note about where the field writes to.
    public bool ShowPendingSync => IsAwaitingSync && !HasError;
    public bool ShowReadOnlyReason => HasReadOnlyReason && !IsAwaitingSync && !HasError;

    /// <summary>Switches the field to writing on-prem, with the caption explaining where its value goes.</summary>
    public void UnlockForOnPrem(string caption)
    {
        WritesToOnPrem = true;
        IsReadOnly = false;
        ReadOnlyReason = caption;
    }

    /// <summary>Keeps the field read-only, with the reason it could not be unlocked.</summary>
    public void KeepLocked(string reason)
    {
        WritesToOnPrem = false;
        IsReadOnly = true;
        ReadOnlyReason = reason;
    }

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private bool _isSaving;

    /// <summary>Brief check mark after a successful write, then it fades on the next edit.</summary>
    [ObservableProperty]
    private bool _justSaved;

    [ObservableProperty]
    private string? _errorText;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    partial void OnErrorTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowPendingSync));
        OnPropertyChanged(nameof(ShowReadOnlyReason));
    }

    partial void OnValueChanged(string value)
    {
        if (JustSaved) JustSaved = false;
        if (HasError) ErrorText = null;
    }

    /// <summary>What the sheet last agreed with the directory. The value an undo puts back.</summary>
    public string CommittedValue => _committed;

    /// <summary>Called on blur and on Enter. Does nothing when the text is unchanged.</summary>
    [RelayCommand]
    private async Task CommitAsync()
    {
        if (IsReadOnly || IsSaving) return;
        var text = (Value ?? string.Empty).Trim();
        if (string.Equals(text, _committed, StringComparison.Ordinal)) return;

        IsSaving = true;
        ErrorText = null;
        try
        {
            object? payload = IsArray
                ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : text.Length == 0 ? null : text;

            var outcome = await _save(PropertyName, payload);
            if (outcome.Success)
            {
                _committed = text;
                Value = text;
                // An on-prem write shows the waiting-for-sync caption instead of the check: the change is real, but
                // claiming it is done while the cloud still shows the old value is the confusion this replaces.
                JustSaved = !IsAwaitingSync;
            }
            else
            {
                ErrorText = outcome.FriendlyError ?? "The change was not saved.";
                Revert();
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Esc, and after a failed save: put the field back to what the directory actually holds.</summary>
    [RelayCommand]
    private void Revert() => Value = _committed;

    /// <summary>Called after an undo or a refresh so the field shows the directory's value again.</summary>
    /// <remarks>
    /// A value written to AD does not come back from Graph until Entra Connect has run, so for a field awaiting sync
    /// the cloud's answer is the stale one. Overwriting with it would look exactly like the edit had been lost. The
    /// field holds what was written until the cloud agrees, and the caption clears itself at that moment.
    /// </remarks>
    public void Reset(string? value)
    {
        var incoming = value ?? string.Empty;
        if (IsAwaitingSync)
        {
            if (!string.Equals(incoming, _committed, StringComparison.Ordinal)) return;
            PendingSyncText = null;
        }

        _committed = incoming;
        Value = _committed;
        JustSaved = false;
        ErrorText = null;
    }

    /// <summary>Accepts a value that is in AD but not yet in the cloud, with the caption saying so.</summary>
    /// <param name="text">The waiting caption to show in place of the done tick.</param>
    /// <param name="writtenValue">
    /// The value now in AD, when the caller is not the commit path that has already put it in the field — an undo,
    /// for instance, which restores a value the field is not currently showing.
    /// </param>
    public void MarkAwaitingSync(string text, string? writtenValue = null)
    {
        if (writtenValue is not null)
        {
            _committed = writtenValue;
            Value = writtenValue;
            ErrorText = null;
        }

        PendingSyncText = text;
        JustSaved = false;
    }
}
