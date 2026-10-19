using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// Settings → Active Directory: connect a tenant to an on-prem domain, or change how it connects. Saves to the tenant's
/// profile in <c>tenant.json</c>; there is deliberately no password field, because a client deployment binds as the
/// admin's own Windows sign-in (doc 11). "Test connection" binds exactly as the saved session will, and lists the real
/// OUs so the leavers OU is picked rather than typed.
/// </summary>
public sealed partial class ConnectDomainDialogViewModel : DialogViewModelBase
{
    private readonly string _tenantName;
    private readonly TenantDomain? _editing;
    private readonly IReadOnlyList<TenantDomain> _others;
    private readonly Func<TenantDomain, LdapOptions> _optionsFor;
    private readonly ILogger<LdapDirectoryService> _ldapLogger;
    private int _testGeneration;
    private bool _ousFromTest;

    public ConnectDomainDialogViewModel(string tenantName, TenantDomain? editing, IReadOnlyList<TenantDomain> others,
        Func<TenantDomain, LdapOptions> optionsFor, ILogger<LdapDirectoryService> ldapLogger, string? suggestedDomain = null)
    {
        _tenantName = tenantName;
        _editing = editing;
        _others = others;
        _optionsFor = optionsFor;
        _ldapLogger = ldapLogger;
        _domain = editing?.Domain ?? suggestedDomain ?? string.Empty;
        _server = editing?.Server ?? string.Empty;
        _useTls = editing?.UseTls ?? false;
        _ouChoices = InitialOus(editing?.DisabledOu);
        _selectedOu = _ouChoices.Count > 1 ? _ouChoices[1] : _ouChoices[0];
        _problem = Check();
    }

    public override string Title => _editing is null ? $"Connect {_tenantName} to Active Directory" : $"Change how {_tenantName} reaches {_editing.Domain}";

    public override string ConfirmText => "Save and reconnect";

    public override double Width => 620;

    public override bool CanConfirm => Problem is null && !IsTesting;

    /// <summary>
    /// Who the connection binds as: the admin's own Windows sign-in, with no password stored — unless this is the lab
    /// domain appsettings names with a credential, which it keeps (<see cref="TenantDomain.ToOptions"/>).
    /// </summary>
    public string BoundAsNote => _optionsFor(Build()).Credential is { } lab
        ? $"Connects with the credential appsettings.local.json gives this domain ({lab.Domain}\\{lab.UserName}): a lab setup. Any other domain connects as you."
        : $"Connects as you ({Environment.UserDomainName}\\{Environment.UserName}), with no password stored. If your AD rights are on a separate account, start the console as that account with runas /netonly.";

    // ---- fields ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DomainHint), nameof(BoundAsNote))]
    private string _domain;

    [ObservableProperty]
    private string _server;

    [ObservableProperty]
    private bool _useTls;

    [ObservableProperty]
    private IReadOnlyList<OuChoice> _ouChoices;

    [ObservableProperty]
    private OuChoice _selectedOu;

    /// <summary>A no-dot name is usually the NetBIOS name, which finds no controller. A hint, not a refusal.</summary>
    public string? DomainHint => TenantDomain.LooksLikeNetBiosName(Domain) && TenantDomain.DomainProblem(Domain) is null
        ? "No dot: that may be the NetBIOS name, which finds no controller. In PowerShell, (Get-ADDomain).DNSRoot shows the DNS name."
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string? _problem;

    partial void OnDomainChanged(string value) => FieldsChanged(domainChanged: true);
    partial void OnServerChanged(string value) => FieldsChanged(domainChanged: false);
    partial void OnUseTlsChanged(bool value) => FieldsChanged(domainChanged: false);

    /// <summary>A test result is about the values it ran with; once they change it no longer says anything.</summary>
    private void FieldsChanged(bool domainChanged)
    {
        Problem = Check();
        if (TestResult is not null)
        {
            _testGeneration++;
            IsTesting = false;
            TestResult = null;
            TestSucceeded = false;
        }

        // Another domain's OUs are not this one's: back to whatever was saved.
        if (domainChanged && _ousFromTest)
        {
            _ousFromTest = false;
            OuChoices = InitialOus(_editing?.DisabledOu);
            SelectedOu = OuChoices.Count > 1 ? OuChoices[1] : OuChoices[0];
        }
    }

    private string? Check()
    {
        if (TenantDomain.DomainProblem(Domain) is { } domainProblem) return domainProblem;
        if (TenantDomain.ServerProblem(Server) is { } serverProblem) return serverProblem;
        if (_others.Any(d => string.Equals(d.Domain.Trim(), Domain.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return $"{_tenantName} is already connected to {Domain.Trim()}. Change that one instead.";
        }

        return null;
    }

    private static IReadOnlyList<OuChoice> InitialOus(string? current)
    {
        var none = new OuChoice(null, "None — offboarding's move to the disabled OU stays blocked");
        return string.IsNullOrWhiteSpace(current) ? [none] : [none, new OuChoice(current, DistinguishedNames.FriendlyPath(current))];
    }

    // ---- test -----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private bool _isTesting;

    [ObservableProperty]
    private string? _testResult;

    [ObservableProperty]
    private bool _testSucceeded;

    [RelayCommand]
    private async Task TestAsync()
    {
        if (Problem is not null || IsTesting) return;

        var generation = ++_testGeneration;
        var domain = Build();
        IsTesting = true;
        TestResult = Server.Trim().Length > 0 ? $"Connecting to {Server.Trim()}…" : $"Looking for a domain controller for {domain.Domain}…";
        TestSucceeded = false;
        try
        {
            var result = await DomainConnectionTest.RunAsync(_optionsFor(domain), _ldapLogger, CancellationToken.None);
            if (generation != _testGeneration) return;

            if (result.Status.IsConnected)
            {
                TestSucceeded = true;
                TestResult = $"Connected to {result.Status.DomainController} as {result.Status.BoundAs}. "
                             + (result.OrganisationalUnits.Count == 1 ? "1 OU found." : $"{result.OrganisationalUnits.Count:N0} OUs found.");
                ApplyOus(result.OrganisationalUnits);
            }
            else
            {
                TestResult = $"{result.Status.Reason} {Advice(domain)}";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation != _testGeneration) return;
            TestResult = $"The test failed: {ex.Message} {Advice(domain)}";
        }
        finally
        {
            if (generation == _testGeneration) IsTesting = false;
        }
    }

    private static string Advice(TenantDomain domain) => domain.Server.Length > 0
        ? $"Check that {domain.Server} is reachable from this PC (the VPN, if you need one) on port {domain.Port}."
        : $"Check the VPN is connected, and that nltest /dsgetdc:{domain.Domain} finds a controller. If it does not, name a domain controller under Server.";

    /// <summary>The real OUs, keeping the saved one selected; one the domain no longer has is kept and marked so.</summary>
    private void ApplyOus(IReadOnlyList<OnPremOu> ous)
    {
        var keep = SelectedOu.Dn;
        var choices = new List<OuChoice> { OuChoices[0] };
        choices.AddRange(ous.Select(o => new OuChoice(o.DistinguishedName, DistinguishedNames.FriendlyPath(o.DistinguishedName))));
        if (keep is not null && !choices.Any(c => string.Equals(c.Dn, keep, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Insert(1, new OuChoice(keep, $"{DistinguishedNames.FriendlyPath(keep)} (not found in the domain)"));
        }

        _ousFromTest = true;
        OuChoices = choices;
        SelectedOu = choices.FirstOrDefault(c => string.Equals(c.Dn, keep, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    }

    // ---- result ---------------------------------------------------------------------------------------------------

    /// <summary>What Save writes: the fields as typed, a port that follows LDAPS unless the file set an unusual one.</summary>
    public TenantDomain Build() => new()
    {
        Domain = Domain.Trim(),
        Server = Server.Trim(),
        UseTls = UseTls,
        Port = _editing is { Port: not 389 and not 636 } custom ? custom.Port : UseTls ? 636 : 389,
        DisabledOu = SelectedOu.Dn ?? string.Empty,
    };
}
