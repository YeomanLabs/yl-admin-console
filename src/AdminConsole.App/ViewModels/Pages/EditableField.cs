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
        IsReadOnly = isReadOnly;
        ReadOnlyReason = readOnlyReason;
        Placeholder = placeholder;
        _save = save;
        _committed = value ?? string.Empty;
        _value = _committed;
    }

    public string Label { get; }
    public string PropertyName { get; }
    public bool IsMono { get; }
    public bool IsArray { get; }
    public bool IsReadOnly { get; }
    public string? ReadOnlyReason { get; }
    public string? Placeholder { get; }
    public bool IsEditable => !IsReadOnly;
    public bool HasReadOnlyReason => !string.IsNullOrEmpty(ReadOnlyReason);

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

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnValueChanged(string value)
    {
        if (JustSaved) JustSaved = false;
        if (HasError) ErrorText = null;
    }

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
                JustSaved = true;
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
    public void Reset(string? value)
    {
        _committed = value ?? string.Empty;
        Value = _committed;
        JustSaved = false;
        ErrorText = null;
    }
}
