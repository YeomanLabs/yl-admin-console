using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One group on the Access tab (UX §4 tab 2): badges plus a remove-X on removable rows. Rows the skip rules block
/// (dynamic, synced, role-assignable) show the reason instead of the X (UX §6).
/// </summary>
public sealed partial class GroupMembershipRow : ObservableObject
{
    private readonly Func<GroupMembershipRow, Task> _remove;

    public GroupMembershipRow(DirectoryGroup group, string? blockedReason, Func<GroupMembershipRow, Task> remove)
    {
        Group = group;
        _remove = remove;
        BlockedReason = group.MembershipChangeBlockedReason() ?? blockedReason;
    }

    public DirectoryGroup Group { get; }
    public string Id => Group.Id;
    public string Label => Group.DisplayName ?? Group.Mail ?? Group.Id;
    public IReadOnlyList<string> Badges => Group.Badges;
    public string MembersText => Group.MemberCount == 1 ? "1 member" : $"{Group.MemberCount} members";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(HasBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private string? _blockedReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _isBusy;

    public bool CanRemove => BlockedReason is null && !IsBusy;
    public bool HasBlockedReason => BlockedReason is not null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        IsBusy = true;
        try
        {
            await _remove(this);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
