using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Auth;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One capability on the Permissions page (UX §12): its state and a Grant button when consent is missing.</summary>
public sealed partial class PermissionRow : ObservableObject
{
    private readonly Func<Capability, Task> _grant;

    public PermissionRow(Capability capability, PermissionResult result, Func<Capability, Task> grant)
    {
        Capability = capability;
        _grant = grant;
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
