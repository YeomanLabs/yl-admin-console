using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.App.Services;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Groups;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Group page (UX §4.5), read-only in M4: header with kind/dynamic/synced badges, members (cache first, then fetched
/// on demand and paged), owners. Membership writes arrive in M5.4.
/// </summary>
public sealed partial class GroupDetailViewModel : ObservableObject, IPageViewModel, IRefreshable, IDisposable
{
    private readonly GroupService _groups;
    private readonly PermissionChecker _permissions;
    private readonly ToastService _toasts;
    private readonly ILogger<GroupDetailViewModel> _logger;
    private CancellationTokenSource? _cts;
    private string? _nextLink;

    public GroupDetailViewModel(GroupService groups, PermissionChecker permissions, ToastService toasts, ILogger<GroupDetailViewModel> logger)
    {
        _groups = groups;
        _permissions = permissions;
        _toasts = toasts;
        _logger = logger;
    }

    public string Id { get; private set; } = string.Empty;
    public string Title => Group?.DisplayName ?? "Group";

    public Action<ObjectType, string, string>? OpenObject { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasGroup))]
    private DirectoryGroup? _group;

    public bool HasGroup => Group is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOwnersLoaded))]
    private GroupLiveDetail? _live;

    public bool IsOwnersLoaded => Live is not null;

    [ObservableProperty]
    private bool _isHydrating;

    [ObservableProperty]
    private string? _hydrateError;

    [ObservableProperty]
    private string _timingText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<BadgeItem> _badges = [];

    [ObservableProperty]
    private string? _membershipRule;

    public bool HasMembershipRule => !string.IsNullOrEmpty(MembershipRule);

    /// <summary>UX §6: why membership cannot be edited here (dynamic / synced / role-assignable).</summary>
    [ObservableProperty]
    private string _membershipDisabledReason = "Adding and removing members arrives in M5.4";

    private void RecomputeHeader()
    {
        if (Group is null) return;
        var list = new List<BadgeItem>(4)
        {
            Group.Kind switch
            {
                GroupKind.M365 => new BadgeItem("M365", BadgeTone.Info),
                GroupKind.Security => new BadgeItem("Security"),
                _ => new BadgeItem("Distribution"),
            },
        };
        if (Group.IsDynamic) list.Add(new BadgeItem("Dynamic", BadgeTone.Warning, DirectoryGroup.DynamicReason));
        if (Group.IsSynced) list.Add(new BadgeItem("Synced", BadgeTone.Info, DirectoryGroup.SyncedReason));
        if (Group.IsAssignableToRole) list.Add(new BadgeItem("Role-assignable", BadgeTone.Warning, DirectoryGroup.RoleAssignableReason));
        Badges = list;

        using var raw = Group.ParseRaw();
        MembershipRule = raw.RootElement.GetString("membershipRule");
        OnPropertyChanged(nameof(HasMembershipRule));

        // The group's own skip rules (dynamic / synced / role-assignable) outrank consent: consenting would not help.
        MembershipDisabledReason = Group.MembershipChangeBlockedReason()
                                   ?? GreyRule.Reason(_permissions, Capabilities.ChangeGroupMembership, "Adding and removing members arrives in M5.4")
                                   ?? string.Empty;
    }

    // ---- members --------------------------------------------------------------------------------------------------

    public ObservableCollection<PersonRef> Members { get; } = [];

    [ObservableProperty]
    private string _membersHeader = "Members";

    [ObservableProperty]
    private bool _isMembersLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreMembersCommand))]
    private bool _hasMoreMembers;

    [ObservableProperty]
    private string? _membersCaption;

    [RelayCommand]
    private void OpenMember(PersonRef? person)
    {
        if (person is null) return;
        var type = person.Kind switch { "group" => ObjectType.Group, "device" => ObjectType.Device, _ => ObjectType.User };
        OpenObject?.Invoke(type, person.Id, person.Label);
    }

    [RelayCommand(CanExecute = nameof(HasMoreMembers))]
    private Task LoadMoreMembers() => LoadMembersAsync(_cts?.Token ?? CancellationToken.None);

    private void SeedMembersFromCache()
    {
        var ids = _groups.GetCachedMemberIds(Id);
        var users = _groups.GetCachedUsers(ids.Where(i => i.MemberType == "user").Select(i => i.MemberId))
            .ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);

        Members.Clear();
        foreach (var (memberId, memberType) in ids.OrderBy(i => users.TryGetValue(i.MemberId, out var u) ? u.DisplayName ?? u.UserPrincipalName : i.MemberId, StringComparer.OrdinalIgnoreCase))
        {
            var user = users.GetValueOrDefault(memberId);
            Members.Add(new PersonRef(memberId, user?.DisplayName, user?.UserPrincipalName, $"#microsoft.graph.{memberType}"));
        }

        MembersCaption = Members.Count == 0 ? null : "Cached membership — refreshing from Graph…";
        UpdateMembersHeader();
    }

    private async Task LoadMembersAsync(CancellationToken ct)
    {
        IsMembersLoading = true;
        try
        {
            var page = await _groups.GetMembersAsync(Id, _nextLink, ct);
            if (ct.IsCancellationRequested) return;
            if (_nextLink is null) Members.Clear();
            foreach (var m in page.Members) Members.Add(m);
            _nextLink = page.NextLink;
            HasMoreMembers = page.HasMore;
            MembersCaption = null;
            UpdateMembersHeader();
        }
        catch (OperationCanceledException)
        {
            // page closed
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Members for {GroupId}: {Code}", Id, ex.ErrorCode);
            MembersCaption = ex.IsForbidden ? "Needs the 'Group.Read.All' permission" : $"Showing cached membership — live read failed ({ex.ErrorCode})";
        }
        finally
        {
            IsMembersLoading = false;
        }
    }

    private void UpdateMembersHeader()
    {
        var known = Members.Count;
        var total = Group?.MemberCount ?? known;
        MembersHeader = HasMoreMembers || known < total ? $"Members ({known} of {total})" : $"Members ({known})";
    }

    // ---- owners ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<PersonRef> _owners = [];

    [ObservableProperty]
    private string? _ownersCaption;

    private void RecomputeOwners()
    {
        if (Live is null) return;
        if (Live.Owners.IsSuccess)
        {
            Owners = Live.Owners.Value!;
            OwnersCaption = Owners.Count == 0 ? "This group has no owners." : null;
        }
        else
        {
            Owners = [];
            OwnersCaption = Live.Owners.IsForbidden ? "Needs the 'Group.Read.All' permission" : $"Could not read owners ({Live.Owners.Error!.ErrorCode})";
        }
    }

    // ---- copy -----------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            _toasts.Show($"Copied {(text.Length <= 48 ? text : text[..45] + "…")}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard unavailable");
            _toasts.Show("Clipboard is unavailable right now");
        }
    }

    // ---- loading --------------------------------------------------------------------------------------------------

    public void Start(string id)
    {
        Id = id;
        _ = LoadAsync(CancellationToken.None);
    }

    public Task RefreshAsync(CancellationToken ct) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _nextLink = null;
        var sw = Stopwatch.StartNew();

        var cached = await Task.Run(() => _groups.GetCached(Id), cts.Token);
        if (cts.IsCancellationRequested) return;
        if (cached is not null) Group = cached;
        RecomputeHeader();
        SeedMembersFromCache();
        var cacheMs = sw.Elapsed.TotalMilliseconds;

        IsHydrating = true;
        HydrateError = null;
        try
        {
            var live = await _groups.HydrateAsync(Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (live.Group.Value is { } fresh) Group = fresh with { MemberCount = cached?.MemberCount ?? 0 };
            else if (live.Group.Error is { } err) HydrateError = err.IsNotFound ? "This group no longer exists." : err.Message;
            Live = live;
            RecomputeHeader();
            RecomputeOwners();
            TimingText = $"cache {cacheMs:F0} ms · live {live.ElapsedMilliseconds:F0} ms";

            // Members on demand (doc 04: never enumerate members per group during sync).
            await LoadMembersAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // page closed or refreshed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Group page {Id}: hydration failed", Id);
            HydrateError = ex is GraphException g ? g.Message : ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsHydrating = false;
        }
    }

    public void Dispose() => _cts?.Cancel();
}
