using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Cache.Browse;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Home (M9.5). It used to be one sentence telling the admin to press Ctrl+K, which is fine if you already know the
/// app and useless otherwise. It is now the place you land and can start from: search, the things people actually do,
/// what is in the tenant, what is worth a look, and what you had open last.
/// </summary>
public sealed partial class HomePageViewModel : ObservableObject, IPageViewModel, IRefreshable
{
    private readonly CacheDb _cache;
    private readonly TenantContext _tenant;
    private readonly ILogger<HomePageViewModel> _logger;

    public HomePageViewModel(CacheDb cache, TenantContext tenant, ILogger<HomePageViewModel> logger)
    {
        _cache = cache;
        _tenant = tenant;
        _logger = logger;
    }

    public string Title => "Home";

    /// <summary>Set by the shell. Home is a launchpad, so almost everything on it is one of the shell's own actions.</summary>
    public Action? OpenPalette { get; set; }

    public Action? NewUser { get; set; }

    public Action? Offboard { get; set; }

    public Action? SyncNow { get; set; }

    /// <summary>Opens a list page with filters already applied.</summary>
    public Action<string, IReadOnlyList<string>>? OpenList { get; set; }

    public Action<ObjectType, string, string>? OpenObject { get; set; }

    [ObservableProperty]
    private string _tenantName = string.Empty;

    /// <summary>First name where there is one. A console a client uses all day should know who is using it.</summary>
    [ObservableProperty]
    private string _greeting = "Admin Console";

    /// <summary>Totals: what is in the tenant.</summary>
    public ObservableCollection<HomeTile> Totals { get; } = [];

    /// <summary>Things that usually want doing something about. Only the ones that are not zero.</summary>
    public ObservableCollection<HomeTile> Attention { get; } = [];

    public ObservableCollection<RecentEntry> Recent { get; } = [];

    public bool HasAttention => Attention.Count > 0;

    /// <summary>Shown instead of the attention tiles when every one of them is zero — which is worth saying out loud
    /// rather than leaving a blank space that looks like something failed to load.</summary>
    public bool NothingNeedsAttention => HasLoaded && Attention.Count == 0;

    public bool HasRecent => Recent.Count > 0;

    [ObservableProperty]
    private bool _hasLoaded;

    [RelayCommand]
    private void Search() => OpenPalette?.Invoke();

    [RelayCommand]
    private void CreateUser() => NewUser?.Invoke();

    [RelayCommand]
    private void OffboardUser() => Offboard?.Invoke();

    [RelayCommand]
    private void Sync() => SyncNow?.Invoke();

    public Task RefreshAsync(CancellationToken ct) => LoadAsync(ct);

    /// <summary>Called by the shell when Home is shown. Reads are off the UI thread; the page paints first.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        TenantName = _tenant.TenantDisplayName;
        Greeting = string.IsNullOrWhiteSpace(_tenant.SignedInDisplayName)
            ? "Admin Console"
            : $"Hello, {_tenant.SignedInDisplayName.Split(' ')[0]}";

        try
        {
            var snapshot = await Task.Run(ReadSnapshot, ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            Totals.Clear();
            foreach (var tile in BuildTotals(snapshot)) Totals.Add(tile);

            Attention.Clear();
            foreach (var tile in BuildAttention(snapshot)) Attention.Add(tile);

            Recent.Clear();
            foreach (var entry in snapshot.Recent) Recent.Add(entry);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Home is the landing page: it must still render if the cache cannot be read.
            _logger.LogError(ex, "Home could not read the cache");
        }
        finally
        {
            HasLoaded = true;
            OnPropertyChanged(nameof(HasAttention));
            OnPropertyChanged(nameof(NothingNeedsAttention));
            OnPropertyChanged(nameof(HasRecent));
        }
    }

    private Snapshot ReadSnapshot()
    {
        var empty = new BrowseQuery();
        var users = _cache.CountUserFilters(empty);
        var devices = _cache.CountDeviceFilters(empty);
        var groups = _cache.CountGroupFilters(empty);

        var recent = new List<RecentEntry>();
        foreach (var r in _cache.GetRecent(8))
        {
            var entry = Describe(r);
            if (entry is not null) recent.Add(entry);
        }

        return new Snapshot(
            _cache.Count(CacheEntity.Users),
            _cache.Count(CacheEntity.Groups),
            _cache.BrowseDevices(new BrowseQuery { Take = 1 }).Total,
            users, devices, groups, recent);
    }

    /// <summary>Turns a recent-object row into something with a name, dropping anything no longer in the cache.</summary>
    private RecentEntry? Describe(RecentObject r)
    {
        switch (r.ObjectType)
        {
            case "user":
                var u = _cache.GetUser(r.ObjectId);
                return u is null ? null : new RecentEntry(ObjectType.User, r.ObjectId, u.DisplayName ?? u.UserPrincipalName, u.UserPrincipalName, Open);
            case "group":
                var g = _cache.GetGroup(r.ObjectId);
                return g is null ? null : new RecentEntry(ObjectType.Group, r.ObjectId, g.DisplayName ?? r.ObjectId, "Group", Open);
            case "device":
                var d = _cache.GetManagedDevice(r.ObjectId);
                if (d is not null) return new RecentEntry(ObjectType.Device, r.ObjectId, d.DeviceName ?? r.ObjectId, d.SerialNumber ?? "Device", Open);
                var e = _cache.GetEntraDevice(r.ObjectId);
                return e is null ? null : new RecentEntry(ObjectType.Device, r.ObjectId, e.DisplayName ?? r.ObjectId, "Device", Open);
            default:
                return null;
        }
    }

    private void Open(RecentEntry entry) => OpenObject?.Invoke(entry.Type, entry.Id, entry.Primary);

    private IEnumerable<HomeTile> BuildTotals(Snapshot s)
    {
        yield return Tile("Users", s.UserCount, "Everyone in the directory", "users", []);
        yield return Tile("Groups", s.GroupCount, "Security, Microsoft 365 and distribution", "groups", []);
        yield return Tile("Devices", s.DeviceCount, "Registered in Entra or enrolled in Intune", "devices", []);
    }

    /// <summary>
    /// The questions worth asking on any given morning. Each is a real filter on a real page, so the tile is not a
    /// figure to look at but a way in. Zeroes are left out: a row of noughts is noise.
    /// </summary>
    private IEnumerable<HomeTile> BuildAttention(Snapshot s)
    {
        var candidates = new (string Label, int Count, string Why, string Nav, string[] Keys)[]
        {
            ("Disabled but still licensed", BothUsers("disabled", "licensed"), "Paying for accounts nobody can sign in to", "users", ["disabled", "licensed"]),
            ("Enabled but unlicensed", BothUsers("enabled", "unlicensed"), "Often a starter half set up, or a leaver half finished", "users", ["enabled", "unlicensed"]),
            ("Guests", s.UserFilters.GetValueOrDefault("guests"), "Invited from outside the tenant", "users", ["guests"]),
            ("Devices not compliant", s.DeviceFilters.GetValueOrDefault("noncompliant"), "Failing at least one Intune policy", "devices", ["noncompliant"]),
            ("Registered but not enrolled", s.DeviceFilters.GetValueOrDefault("entraonly"), "In Entra, never enrolled in Intune — nothing can be done to them remotely", "devices", ["entraonly"]),
            ("Devices quiet 90+ days", s.DeviceFilters.GetValueOrDefault("stale90"), "No check-in for a quarter — often gone", "devices", ["stale90"]),
            ("Empty groups", s.GroupFilters.GetValueOrDefault("empty"), "No members — often left behind after a project", "groups", ["empty"]),
        };

        return candidates
            .Where(c => c.Count > 0)
            .Select(c => Tile(c.Label, c.Count, c.Why, c.Nav, c.Keys, attention: true));
    }

    /// <summary>
    /// Two filters from different groups AND together, so the chip counts cannot simply be added — this asks the
    /// cache for the pair.
    /// </summary>
    private int BothUsers(string first, string second) =>
        _cache.BrowseUsers(new BrowseQuery { FilterKeys = [first, second], Take = 1 }).Total;

    private HomeTile Tile(string label, int count, string why, string nav, IReadOnlyList<string> keys, bool attention = false) =>
        new(label, count, why, nav, keys, t => OpenList?.Invoke(t.NavId, t.FilterKeys)) { IsAttention = attention };

    private sealed record Snapshot(
        int UserCount,
        int GroupCount,
        int DeviceCount,
        IReadOnlyDictionary<string, int> UserFilters,
        IReadOnlyDictionary<string, int> DeviceFilters,
        IReadOnlyDictionary<string, int> GroupFilters,
        IReadOnlyList<RecentEntry> Recent);
}
