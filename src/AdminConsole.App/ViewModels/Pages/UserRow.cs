using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Cache.Browse;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>A user as one row of the Users list. Wraps the cache row so the wording lives in the App, not in Core.</summary>
public sealed partial class UserRow : ObservableObject
{
    private readonly Action<UserRow, bool>? _selectionChanged;

    public UserRow(UserListRow row, string? extra = null, bool isSelected = false, Action<UserRow, bool>? selectionChanged = null)
    {
        Model = row;
        Extra = extra;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public UserListRow Model { get; }

    /// <summary>The chosen columns the row does not otherwise draw (M18.5): "Mail: … · Created: …".</summary>
    public string? Extra { get; }

    public bool HasExtra => !string.IsNullOrEmpty(Extra);

    public string Id => Model.Id;
    public string Primary => Model.Primary;
    public string Upn => Model.Upn;
    public string? JobTitle => Model.JobTitle;
    public string? Department => Model.Department;
    public bool Enabled => Model.Enabled;
    public bool Licensed => Model.Licensed;
    public bool Synced => Model.Synced;
    public bool IsGuest => Model.IsGuest;

    /// <summary>Ticked for a bulk edit (M18.3). The page keeps the set by id, so it survives a re-sort or a reload.</summary>
    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke(this, value);

    public string SelectName => $"Select {Primary}";

    public override string ToString() => Primary;
}
