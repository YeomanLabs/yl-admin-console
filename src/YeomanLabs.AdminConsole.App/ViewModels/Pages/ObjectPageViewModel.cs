using CommunityToolkit.Mvvm.ComponentModel;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Stand-in object page opened from the palette until User 360 / Device / Group pages arrive in M4. Carries what those
/// pages will need (type + id + cached display text) so the palette → tab → back-stack path is exercised end to end now.
/// </summary>
public sealed partial class ObjectPageViewModel : ObservableObject, IPageViewModel
{
    public ObjectPageViewModel(SearchHit hit)
    {
        ObjectType = hit.ObjectType;
        Id = hit.Id;
        Title = hit.Primary;
        Secondary = hit.Secondary;
        Badges = hit.Badges;
    }

    public ObjectType ObjectType { get; }
    public string Id { get; }
    public string Title { get; }
    public string? Secondary { get; }
    public IReadOnlyList<string> Badges { get; }

    public string Kind => ObjectType switch
    {
        ObjectType.User => "User",
        ObjectType.Device => "Intune device",
        ObjectType.Group => "Group",
        _ => ObjectType.ToString(),
    };

    public string ArrivesIn => ObjectType switch
    {
        ObjectType.User => "User 360 arrives in M4.",
        ObjectType.Device => "The device page arrives in M4.",
        ObjectType.Group => "The group page arrives in M4.",
        _ => string.Empty,
    };
}
