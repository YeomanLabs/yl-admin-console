using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YeomanLabs.AdminConsole.Core.Auth;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Shown before sign-in when this installation does not yet know which app registration to use (M10.2).
/// </summary>
/// <remarks>
/// Without this, an unconfigured install fails somewhere inside MSAL with a message about an application identifier,
/// which tells the person in front of it nothing they can act on. This asks the one question that needs answering and
/// says where the answer comes from.
/// </remarks>
public sealed partial class SetupPageViewModel : ObservableObject, IPageViewModel
{
    private readonly TenantConfigStore _store;

    public SetupPageViewModel(TenantConfigStore store, Action onSaved)
    {
        _store = store;
        _onSaved = onSaved;

        var existing = store.Load();
        _clientId = existing.ClientId;
        _singleTenant = !existing.Authority.EndsWith("/organizations", StringComparison.OrdinalIgnoreCase);
        _tenantId = _singleTenant ? existing.Authority.Split('/').LastOrDefault() ?? string.Empty : string.Empty;
    }

    private readonly Action _onSaved;

    public string Title => "Setup";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(ClientIdError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _clientId = string.Empty;

    /// <summary>Restricts sign-in to one tenant. Off by default: the multi-tenant authority works everywhere.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _singleTenant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _tenantId = string.Empty;

    /// <summary>Shown under the box as soon as there is something wrong to say, and not before.</summary>
    public string? ClientIdError =>
        string.IsNullOrWhiteSpace(ClientId) || TenantConfig.LooksLikeClientId(ClientId)
            ? null
            : "That does not look like a client ID. It is a GUID, like 61ef07ab-e035-49e2-b05e-cfd25fc1c1fe.";

    public bool CanSave =>
        TenantConfig.LooksLikeClientId(ClientId) && (!SingleTenant || !string.IsNullOrWhiteSpace(TenantId));

    /// <summary>The command a client runs to produce the client ID, so it can be copied straight out of the screen.</summary>
    public string ScriptCommand => ".\\scripts\\New-AppRegistration.ps1 -WhatIf";

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var authority = SingleTenant
            ? $"https://login.microsoftonline.com/{TenantId.Trim()}"
            : new TenantConfig().Authority;

        _store.Save(new TenantConfig
        {
            ClientId = ClientId.Trim(),
            Authority = authority,
            TenantHint = SingleTenant ? TenantId.Trim() : null,
        });

        _onSaved();
    }
}
