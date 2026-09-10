using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Models;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>Something the admin had open recently, on Home. Picking up where you left off is most of what a second
/// visit to a console is.</summary>
public sealed class RecentEntry
{
    public RecentEntry(ObjectType type, string id, string primary, string secondary, Action<RecentEntry> open)
    {
        Type = type;
        Id = id;
        Primary = primary;
        Secondary = secondary;
        OpenCommand = new RelayCommand(() => open(this));
    }

    public ObjectType Type { get; }
    public string Id { get; }
    public string Primary { get; }
    public string Secondary { get; }
    public IRelayCommand OpenCommand { get; }

    public string KindText => Type switch
    {
        ObjectType.User => "Person",
        ObjectType.Group => "Group",
        ObjectType.Device => "Device",
        _ => string.Empty,
    };
}
