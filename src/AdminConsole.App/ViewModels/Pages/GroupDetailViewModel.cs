using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Groups;
using AdminConsole.Core.Models;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// Group page (UX §4.5): header with kind/dynamic/synced badges, members (cache first, then fetched on demand and
/// paged), owners. Since 0.3.6 members are added and removed here too, through the same GroupService writes the person
/// page uses — the skip rules (dynamic, synced, role-assignable) are enforced there as well as greyed here.
/// </summary>
public sealed partial class GroupDetailViewModel : ObservableObject, IPageViewModel, IRefreshable, IDisposable
{
    private readonly GroupService _groups;
    private readonly PermissionChecker _permissions;
    private readonly ToastService _toasts;
    private readonly ILogger<GroupDetailViewModel> _logger;
    private readonly TenantContext _tenant;
    private readonly ConsentService _consent;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;
    private string? _nextLink;

    public GroupDetailViewModel(GroupService groups, PermissionChecker permissions, ToastService toasts, ILogger<GroupDetailViewModel> logger,
        TenantContext tenant, ConsentService consent, IDialogService dialogs)
    {
        _groups = groups;
        _permissions = permissions;
        _toasts = toasts;
        _logger = logger;
        _tenant = tenant;
        _consent = consent;
        _dialogs = dialogs;
    }

    /// <summary>Set by the shell: opens the palette restricted to users and calls back with the pick.</summary>
    public Action<string, Action<PersonRef>>? PickPerson { get; set; }

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

    /// <summary>The caption under Add member: the group's own block, else the consent the first change will ask for.</summary>
    [ObservableProperty]
    private string _membershipDisabledReason = string.Empty;

    /// <summary>The group's own rules (dynamic, synced, role-assignable without the role): a real block, which consent would not lift.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditMembers))]
    private string? _membershipBlockedReason;

    /// <summary>Add and remove are live unless the group's own rules forbid it; a missing permission is asked for on the click.</summary>
    public bool CanEditMembers => HasGroup && MembershipBlockedReason is null;

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
        MembershipBlockedReason = _groups.MembershipBlockedReason(Group, _tenant.IsGlobalAdmin || _tenant.HasRole(DirectoryRoleRef.PrivilegedRoleAdministrator));
        MembershipDisabledReason = MembershipBlockedReason
                                   ?? GreyRule.Reason(_permissions, Capabilities.ChangeGroupMembership, null)
                                   ?? string.Empty;
        OnPropertyChanged(nameof(CanEditMembers));
    }

    // ---- membership changes ---------------------------------------------------------------------------------------

    [RelayCommand]
    private void AddMember()
    {
        if (!CanEditMembers || Group is null) return;
        PickPerson?.Invoke($"Choose who to add to {Group.DisplayName}", person => _ = AddMemberAsync(person));
    }

    private async Task AddMemberAsync(PersonRef person)
    {
        if (Members.Any(m => string.Equals(m.Id, person.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _toasts.Show($"{person.Label} is already a member.");
            return;
        }

        if (!await EnsureAsync()) return;
        var outcome = await _groups.AddMemberAsync(Id, person.Id, person.Label, CancellationToken.None);
        await AfterChangeAsync(outcome);
    }

    /// <summary>Remove a member, after saying what they lose; undo adds them back.</summary>
    [RelayCommand]
    private async Task RemoveMemberAsync(PersonRef? person)
    {
        if (person is null || !CanEditMembers || Group is null) return;

        var confirmed = await _dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Heading = $"Remove {person.Label} from {Group.DisplayName}?",
            Message = "They lose whatever this group gives them — access, licences, apps and policies. Undo puts them back.",
            ConfirmLabel = "Remove from group",
            Destructive = true,
        });
        if (!confirmed) return;
        if (!await EnsureAsync()) return;

        var outcome = await _groups.RemoveMemberAsync(Id, person.Id, person.Label, CancellationToken.None);
        await AfterChangeAsync(outcome);
    }

    private async Task AfterChangeAsync(Core.Writes.WriteOutcome outcome)
    {
        if (!outcome.Success)
        {
            _toasts.Show(outcome.FriendlyError ?? outcome.Summary);
            return;
        }

        _toasts.Show(outcome.Summary, "Undo", async () =>
        {
            var back = await outcome.Undo!(CancellationToken.None);
            _toasts.Show(back.Success ? back.Summary : back.FriendlyError ?? "Undo failed");
            await ReloadMembersAsync();
        });
        await ReloadMembersAsync();
    }

    /// <summary>The member list and count again: the write updated the cache, and Graph has the order.</summary>
    private async Task ReloadMembersAsync()
    {
        if (_groups.GetCached(Id) is { } cached && Group is not null) Group = Group with { MemberCount = cached.MemberCount };
        _nextLink = null;
        await LoadMembersAsync(_cts?.Token ?? CancellationToken.None);
    }

    /// <summary>Consent on the first change of the session; a refusal says why (ConsentFailure).</summary>
    private async Task<bool> EnsureAsync()
    {
        var permission = _permissions.Check(Capabilities.ChangeGroupMembership);
        if (!permission.NeedsConsent) return true;
        try
        {
            permission = await _consent.EnsureAsync(Capabilities.ChangeGroupMembership, CancellationToken.None);
        }
        catch (SignInException ex)
        {
            await ConsentFailure.ShowAsync(_dialogs, _toasts, ex);
            return false;
        }

        if (!permission.CanRun) _toasts.Show(permission.Reason ?? "Not permitted.");
        return permission.CanRun;
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
