using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Pim;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One of my PIM roles on the Settings page (M18.2), with Activate when it is eligible and not active.</summary>
public sealed partial class PimRoleRow
{
    private readonly Func<PimRole, Task> _activate;

    public PimRoleRow(PimRole role, Func<PimRole, Task> activate)
    {
        Role = role;
        _activate = activate;
    }

    public PimRole Role { get; }

    public string Name => Role.DisplayName;

    public string StateText => Role.ViaGroup ? Role.StateText + " · through a group" : Role.StateText;

    public bool CanActivate => !Role.IsActive && !Role.IsPermanent;

    public string ActivateName => $"Activate {Role.DisplayName}";

    [RelayCommand]
    private Task ActivateAsync() => _activate(Role);

    public override string ToString() => $"{Name}: {StateText}";
}
