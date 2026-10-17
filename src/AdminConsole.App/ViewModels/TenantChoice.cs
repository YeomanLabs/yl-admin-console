using CommunityToolkit.Mvvm.Input;

namespace AdminConsole.App.ViewModels;

/// <summary>One tenant in the title-bar switcher (M20.2).</summary>
public sealed partial class TenantChoice
{
    private readonly Func<TenantChoice, Task> _open;

    public TenantChoice(string profileId, string name, string detail, bool isActive, Func<TenantChoice, Task> open)
    {
        ProfileId = profileId;
        Name = name;
        Detail = detail;
        IsActive = isActive;
        _open = open;
    }

    public string ProfileId { get; }
    public string Name { get; }

    /// <summary>"Open · 3 tabs", "Not open".</summary>
    public string Detail { get; }

    public bool IsActive { get; }

    public string AccessibleName => $"{Name}, {Detail}{(IsActive ? ", current" : string.Empty)}";

    [RelayCommand]
    private Task OpenAsync() => _open(this);

    public override string ToString() => AccessibleName;
}
