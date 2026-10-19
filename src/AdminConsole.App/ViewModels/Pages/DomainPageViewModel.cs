using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// The on-prem Active Directory page (M12.3): what is in the domain, and whether the console can see it at all.
/// </summary>
/// <remarks>
/// Being off the corporate network is the normal state of a laptop, not an error, so an unreachable domain is a
/// first-class thing this page says plainly — with the reason — rather than an empty list or a stack trace.
/// </remarks>
public sealed partial class DomainPageViewModel : ObservableObject, IPageViewModel, IRefreshable
{
    private const int SearchLimit = 200;

    private readonly IDirectoryService _directory;
    private readonly ILogger<DomainPageViewModel> _logger;
    private CancellationTokenSource? _pending;

    public DomainPageViewModel(IDirectoryService directory, ILogger<DomainPageViewModel> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public string Title => "Domain";

    /// <summary>Set by the shell: opens the AD object a row points at.</summary>
    public Action<Guid, OnPremKind>? OpenObject { get; set; }

    /// <summary>Set by the shell: the connect dialog, for a tenant with no domain yet.</summary>
    public Func<Task>? RequestConnect { get; set; }

    /// <summary>Set by the shell: Settings, where the tenant's domains are listed and changed.</summary>
    public Action? RequestSettings { get; set; }

    // ---- connection ----------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(IsNotConfigured))]
    [NotifyPropertyChangedFor(nameof(EmptyTitle))]
    [NotifyPropertyChangedFor(nameof(NeedsSignIn))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    private DirectoryStatus _status = DirectoryStatus.NotConfigured;

    /// <summary>The domain connects as another account whose password this run has not been given (or was refused).</summary>
    public bool NeedsSignIn => Status.NeedsSignIn;

    /// <summary>Configured but not answering: worth another go once the VPN is up.</summary>
    public bool CanRetry => !Status.IsConnected && !IsNotConfigured && !Status.NeedsSignIn;

    /// <summary>Sign in / Try again: forget the failure (and a declined sign-in), and connect now.</summary>
    [RelayCommand]
    private Task RetryAsync()
    {
        _directory.Reconnect();
        return RefreshAsync(CancellationToken.None);
    }

    public bool IsConnected => Status.IsConnected;

    /// <summary>No domain named at all, as opposed to one named that did not answer: the page offers to connect one.</summary>
    public bool IsNotConfigured => Status == DirectoryStatus.NotConfigured;

    public string EmptyTitle => IsNotConfigured ? "Not connected to Active Directory" : Status.NeedsSignIn ? "Not signed in to Active Directory" : "No domain controller in reach";

    [RelayCommand]
    private Task ConnectAsync() => RequestConnect?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenAdSettings() => RequestSettings?.Invoke();

    public string SubtitleText => Status.IsConnected
        ? $"{Status.DomainName} · via {Status.DomainController} · as {Status.BoundAs}"
        : Status.Reason ?? "Not connected to a domain.";

    // ---- what is being looked at ---------------------------------------------------------------------------------

    public IReadOnlyList<string> Kinds { get; } = ["Users", "Groups", "Computers"];

    [ObservableProperty]
    private string _kind = "Users";

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnKindChanged(string value) => QueueReload();

    partial void OnSearchTextChanged(string value) => QueueReload();

    // ---- results -------------------------------------------------------------------------------------------------

    public ObservableCollection<DomainRow> Rows { get; } = [];

    public ObservableCollection<OuNode> OuRoots { get; } = [];

    /// <summary>The OU tree flattened for a list, since WPF draws an indented list more simply than a real tree.</summary>
    public ObservableCollection<OuNode> OuList { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _total;

    public string CountText => Total switch
    {
        0 => string.Empty,
        1 => "1 result",
        _ when Total >= SearchLimit => $"{SearchLimit}+ results — narrow the search",
        _ => $"{Total:N0} results",
    };

    public bool IsEmpty => HasLoaded && IsConnected && Rows.Count == 0;

    public string EmptyText => string.IsNullOrWhiteSpace(SearchText)
        ? $"No {Kind.ToLowerInvariant()} found in this domain."
        : $"No {Kind.ToLowerInvariant()} match “{SearchText}”.";

    public Task RefreshAsync(CancellationToken ct) => LoadAsync(ct);

    /// <summary>Called by the shell when the page is shown.</summary>
    public void Start() => QueueReload();

    private void QueueReload()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        var cts = new CancellationTokenSource();
        _pending = cts;
        _ = DebouncedAsync(cts.Token);
    }

    private async Task DebouncedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), ct).ConfigureAwait(true);
            await LoadAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later keystroke.
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            Status = await _directory.GetStatusAsync(ct).ConfigureAwait(true);
            if (!Status.IsConnected)
            {
                Rows.Clear();
                OuList.Clear();
                Total = 0;
                return;
            }

            var rows = Kind switch
            {
                "Groups" => (await _directory.SearchGroupsAsync(SearchText, SearchLimit, SelectedOu?.DistinguishedName, ct).ConfigureAwait(true))
                    .Select(DomainRow.From).ToList(),
                "Computers" => (await _directory.SearchComputersAsync(SearchText, SearchLimit, SelectedOu?.DistinguishedName, ct).ConfigureAwait(true))
                    .Select(DomainRow.From).ToList(),
                _ => (await _directory.SearchUsersAsync(SearchText, SearchLimit, SelectedOu?.DistinguishedName, ct).ConfigureAwait(true))
                    .Select(DomainRow.From).ToList(),
            };

            ct.ThrowIfCancellationRequested();
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            Total = rows.Count;

            if (OuList.Count == 0)
            {
                var ous = await _directory.GetOrganisationalUnitsAsync(ct).ConfigureAwait(true);
                var roots = OuNode.Build(ous);
                OuRoots.Clear();
                OuList.Clear();
                // Collapsed to start: the top level only, and the admin opens the branch they want.
                foreach (var root in roots) OuRoots.Add(root);
                foreach (var node in OuNode.Visible(roots)) OuList.Add(node);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the domain");
            Rows.Clear();
            Total = 0;
        }
        finally
        {
            IsLoading = false;
            HasLoaded = true;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(CountText));
        }
    }

    [RelayCommand]
    private void Open(DomainRow row) => OpenObject?.Invoke(row.ObjectGuid, row.Kind);

    [RelayCommand]
    private void FilterToOu(OuNode node)
    {
        // Clicking an OU narrows to it. An LDAP subtree search includes everything beneath, which is what an admin
        // means by "in Sales" — clicking the parent should not hide what is in its children.
        SelectedOu = ReferenceEquals(SelectedOu, node) ? null : node;
        QueueReload();
    }

    /// <summary>Opens or closes an OU's branch in place, so the list does not jump back to the top.</summary>
    [RelayCommand]
    private void ToggleOu(OuNode node)
    {
        var at = OuList.IndexOf(node);
        if (at < 0 || !node.HasChildren) return;

        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            while (at + 1 < OuList.Count && OuList[at + 1].Depth > node.Depth) OuList.RemoveAt(at + 1);
            return;
        }

        node.IsExpanded = true;
        var insert = at + 1;
        foreach (var child in OuNode.Visible(node.Children)) OuList.Insert(insert++, child);
    }

    [RelayCommand]
    private void ClearOuFilter()
    {
        SelectedOu = null;
        QueueReload();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeText))]
    [NotifyPropertyChangedFor(nameof(HasOuFilter))]
    private OuNode? _selectedOu;

    public bool HasOuFilter => SelectedOu is not null;

    /// <summary>What the list is currently showing, so a filtered view never looks like the whole domain.</summary>
    public string ScopeText => SelectedOu is null
        ? "Everywhere in the domain"
        : $"In {SelectedOu.Name} and below";
}
