using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Users;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One attribute the "Stamp offboarding attributes" step may write: leave it, clear it, or set it to a value. Only the
/// attributes the snapshot records are offered, so whatever a template stamps, Restore can put back.
/// </summary>
public sealed partial class StampRow : ObservableObject
{
    private readonly Action _changed;

    public StampRow(string attribute, string? value, bool stamped, Action changed)
    {
        Attribute = attribute;
        _changed = changed;
        _mode = !stamped ? StampMode.Leave : string.IsNullOrEmpty(value) ? StampMode.Clear : StampMode.Set;
        _value = value ?? string.Empty;
    }

    public string Attribute { get; }

    /// <summary>"Employee type", from the same words the rest of the console uses for the property.</summary>
    public string Label
    {
        get
        {
            var friendly = UserService.FriendlyProperty(Attribute);
            return friendly.Length == 0 ? Attribute : char.ToUpperInvariant(friendly[0]) + friendly[1..];
        }
    }

    public static IReadOnlyList<StampModeChoice> Modes { get; } =
    [
        new(StampMode.Leave, "Leave as it is"),
        new(StampMode.Clear, "Clear it"),
        new(StampMode.Set, "Set it to"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValueEnabled))]
    private StampMode _mode;

    [ObservableProperty]
    private string _value;

    public bool IsValueEnabled => Mode == StampMode.Set;

    /// <summary>A "Set it to" with nothing typed, which would be a clear the admin did not ask for.</summary>
    public bool IsIncomplete => Mode == StampMode.Set && Value.Trim().Length == 0;

    partial void OnModeChanged(StampMode value) => _changed();

    partial void OnValueChanged(string value) => _changed();

    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => Mode switch
    {
        StampMode.Clear => $"{Label}: clear it",
        StampMode.Set => $"{Label}: set it to {Value}",
        _ => $"{Label}: leave as it is",
    };
}
