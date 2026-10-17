using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Users.BulkEdit;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>One field on the bulk edit sheet: ticked to change it, and the value everyone gets.</summary>
public sealed partial class BulkFieldRow : ObservableObject
{
    private readonly Action _changed;

    public BulkFieldRow(BulkField field, Action changed)
    {
        Field = field;
        _changed = changed;
    }

    public BulkField Field { get; }

    public string Label => Field.Label;

    public string ChangeName => $"Change {Field.Label.ToLowerInvariant()}";

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private string _value = string.Empty;

    partial void OnIsOnChanged(bool value) => _changed();

    partial void OnValueChanged(string value)
    {
        // Typing a value is how most people say "change this", so it ticks the box rather than making them do both.
        if (value.Length > 0 && !IsOn) IsOn = true;
        else _changed();
    }

    public override string ToString() => Label;
}
