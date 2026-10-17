using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.OneDrive;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One site admin row with its remove button, or the reason there isn't one.</summary>
public sealed partial class OneDriveAdminRow : ObservableObject
{
    private readonly Func<OneDriveAdminRow, Task> _remove;

    public OneDriveAdminRow(SiteAdmin admin, bool isOwner, string? blockedReason, Func<OneDriveAdminRow, Task> remove)
    {
        Admin = admin;
        IsOwner = isOwner;
        BlockedReason = blockedReason;
        _remove = remove;
    }

    public SiteAdmin Admin { get; }
    public bool IsOwner { get; }
    public string Label => IsOwner ? $"{Admin.Label} · owner" : Admin.Label;
    public string? BlockedReason { get; }
    public bool CanRemove => BlockedReason is null;
    public string RemoveName => $"Remove {Admin.Label} as admin";

    [RelayCommand]
    private Task RemoveAsync() => _remove(this);
}
