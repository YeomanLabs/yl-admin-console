using System.Globalization;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.Core.Cache.Browse;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>A user as one row of the Users list. Wraps the cache row so the wording lives in the App, not in Core.</summary>
public sealed class UserRow(UserListRow row)
{
    public UserListRow Model { get; } = row;

    public string Id => Model.Id;
    public string Primary => Model.Primary;
    public string Upn => Model.Upn;
    public string? JobTitle => Model.JobTitle;
    public string? Department => Model.Department;
    public bool Enabled => Model.Enabled;
    public bool Licensed => Model.Licensed;
    public bool Synced => Model.Synced;
    public bool IsGuest => Model.IsGuest;
}
