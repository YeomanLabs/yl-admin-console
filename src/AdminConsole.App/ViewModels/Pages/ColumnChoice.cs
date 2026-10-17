using CommunityToolkit.Mvvm.ComponentModel;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One column in a list's column chooser (M18.5).</summary>
public sealed partial class ColumnChoice : ObservableObject
{
    private readonly Action _changed;

    public ColumnChoice(string key, string label, bool isOn, Action changed)
    {
        Key = key;
        Label = label;
        _isOn = isOn;
        _changed = changed;
    }

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value) => _changed();

    public override string ToString() => Label;
}
