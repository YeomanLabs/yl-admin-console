using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One line of the Mailbox tab (M14): a label and value like <see cref="FactRow"/>, plus what can be done to it — an
/// action button, a remove button, or (folder permissions) a role to change. A row with none of these is read-only.
/// </summary>
public sealed partial class MailboxRow : ObservableObject
{
    private readonly Func<string, Task>? _changeRole;
    private readonly string? _originalRole;

    public MailboxRow(string label, string value, bool isMono = false, string? caption = null)
    {
        Label = label;
        Value = value;
        IsMono = isMono;
        Caption = caption;
    }

    /// <summary>A folder-permission row: <paramref name="changeRole"/> runs when the admin picks a different role.</summary>
    public MailboxRow(string label, string role, IReadOnlyList<string> roles, Func<string, Task> changeRole, string? caption = null)
        : this(label, role, caption: caption)
    {
        Roles = roles;
        _originalRole = role;
        _role = role;
        _changeRole = changeRole;
    }

    public string Label { get; }
    public string Value { get; }
    public bool IsMono { get; }
    public string? Caption { get; }

    /// <summary>The verb on the row's button, e.g. "Make primary". Null for no button.</summary>
    public string? ActionLabel { get; init; }

    public IAsyncRelayCommand? Action { get; init; }

    /// <summary>Why the action is unavailable, shown instead of the button — never a silently dead control (UX §6).</summary>
    public string? ActionBlockedReason { get; init; }

    public IAsyncRelayCommand? Remove { get; init; }

    /// <summary>The remove button's accessible name, e.g. "Remove sales@contoso.com".</summary>
    public string RemoveName => $"Remove {Value}";

    public IReadOnlyList<string>? Roles { get; }

    public bool HasAction => Action is not null;
    public bool HasRemove => Remove is not null;
    public bool HasRoles => Roles is not null;
    public bool HasBlockedReason => ActionBlockedReason is not null;

    [ObservableProperty]
    private string? _role;

    partial void OnRoleChanged(string? value)
    {
        if (value is not null && _changeRole is not null && !string.Equals(value, _originalRole, StringComparison.OrdinalIgnoreCase))
        {
            _ = _changeRole(value);
        }
    }

    /// <summary>What a screen reader announces for the row.</summary>
    public override string ToString() => Caption is null ? $"{Label}: {Value}" : $"{Label}: {Value}. {Caption}";
}
