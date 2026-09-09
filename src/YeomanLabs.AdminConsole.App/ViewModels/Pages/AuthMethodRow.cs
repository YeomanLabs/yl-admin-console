using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One registered method on the Access tab (UX §4 tab 2): type, detail, and a delete-X on the ones Graph lets us
/// remove. The password row shows no X at all, and a missing permission puts its reason in <see cref="BlockedReason"/>.
/// </summary>
public sealed partial class AuthMethodRow : ObservableObject
{
    private readonly Func<AuthMethodRow, Task> _delete;

    public AuthMethodRow(AuthMethod method, string? blockedReason, Func<AuthMethodRow, Task> delete)
    {
        Method = method;
        _delete = delete;
        _blockedReason = blockedReason;
    }

    public AuthMethod Method { get; }
    public string Id => Method.Id;
    public string KindLabel => Method.KindLabel;
    public string DisplayName => Method.DisplayName;
    public string? Detail => Method.Detail;

    /// <summary>The password method is not deletable, so its row shows nothing rather than a greyed X (UX §6).</summary>
    public bool IsDeletable => Method.CanDelete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(HasBlockedReason))]
    private string? _blockedReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isBusy;

    public bool CanDelete => IsDeletable && BlockedReason is null && !IsBusy;
    public bool HasBlockedReason => BlockedReason is not null;

    [RelayCommand]
    private async Task DeleteAsync()
    {
        IsBusy = true;
        try
        {
            await _delete(this);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
