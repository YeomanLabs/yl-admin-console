using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Auth;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One capability on the Permissions page (UX §12): its state and a Grant button when consent is missing.</summary>
public sealed partial class PermissionRow : ObservableObject
{
    private readonly Func<Capability, Task> _grant;
    private readonly Func<DirectoryRoleRef, Task>? _activate;

    public PermissionRow(Capability capability, PermissionResult result, Func<Capability, Task> grant, Func<DirectoryRoleRef, Task>? activate = null)
    {
        Capability = capability;
        _grant = grant;
        _activate = activate;
        Apply(result);
    }

    public Capability Capability { get; }
    public string Title => Capability.Title;
    public string ScopeList => string.Join(", ", Capability.Scopes);
    public bool IsSensitive => Capability.IsSensitive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    [NotifyPropertyChangedFor(nameof(NeedsConsent))]
    [NotifyPropertyChangedFor(nameof(HasRoleWarning))]
    [NotifyCanExecuteChangedFor(nameof(GrantCommand))]
    private PermissionState _state;

    [ObservableProperty]
    private string? _reason;

    /// <summary>M18.2: the missing role is PIM-eligible, so the row offers activation rather than a dead end.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActivateRole), nameof(ActivateName))]
    private DirectoryRoleRef? _eligibleRole;

    public bool CanActivateRole => EligibleRole is not null && _activate is not null;

    public string ActivateName => EligibleRole is { } r ? $"Activate {r.DisplayName}" : string.Empty;

    [RelayCommand]
    private async Task ActivateRoleAsync()
    {
        if (EligibleRole is { } role && _activate is not null) await _activate(role);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GrantCommand))]
    private bool _isGranting;

    public bool IsGranted => State == PermissionState.Allowed;
    public bool NeedsConsent => State == PermissionState.NeedsConsent;
    public bool HasRoleWarning => State == PermissionState.AllowedWithRoleWarning;

    public void Apply(PermissionResult result)
    {
        State = result.State;
        Reason = result.Reason;
        EligibleRole = result.EligibleRole;
    }

    [RelayCommand(CanExecute = nameof(CanGrant))]
    private async Task GrantAsync()
    {
        IsGranting = true;
        try
        {
            await _grant(Capability);
        }
        finally
        {
            IsGranting = false;
        }
    }

    private bool CanGrant() => NeedsConsent && !IsGranting;
}
