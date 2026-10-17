using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Models;
using AdminConsole.Core.Writes;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// User 360's Mailbox tab (M13.5 read, M14 write; doc 13). Loads the first time the tab is selected. Sections load in
/// parallel and fail on their own. Every change goes through <see cref="RunAsync"/>: permission first, one write at a time
/// (two quick writes to one mailbox can land out of order — doc 13 §5), a toast with Undo where the change can be undone,
/// then a quiet reload so the page shows what Exchange now holds rather than what was asked for.
/// </summary>
public sealed partial class MailboxTabViewModel : ObservableObject, IDisposable
{
    private const string SyncedReason = "Synced from on-prem AD: change this in AD and it syncs up. Writing it to AD from here is not built yet.";

    private readonly ExchangeService _exchange;
    private readonly ConsentService _consent;
    private readonly PermissionChecker _permissions;
    private readonly CacheDb _db;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ILogger<MailboxTabViewModel> _logger;
    private string _userId = string.Empty;
    private bool _requested;
    private CancellationTokenSource? _cts;
    private MailboxDetail? _detail;
    private AutoReplySettings? _autoReply;
    private RegionalSettings? _regional;
    private IReadOnlyList<string>? _domains;

    public MailboxTabViewModel(ExchangeService exchange, ConsentService consent, PermissionChecker permissions, CacheDb db,
        IDialogService dialogs, ToastService toasts, ILogger<MailboxTabViewModel> logger)
    {
        _exchange = exchange;
        _consent = consent;
        _permissions = permissions;
        _db = db;
        _dialogs = dialogs;
        _toasts = toasts;
        _logger = logger;
        FolderChoices = [.. MailboxFolder.Delegable.Select(f => new FolderChoice(f.FolderType, f.Label))];
        _selectedFolder = FolderChoices[0];
    }

    /// <summary>Set by User 360 from the shell: opens the palette restricted to people and hands back the pick.</summary>
    public Action<string, Action<PersonRef>>? PickPerson { get; set; }

    // ---- state -----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSections))]
    [NotifyPropertyChangedFor(nameof(ShowStateMessage))]
    [NotifyPropertyChangedFor(nameof(ShowGrant))]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    private MailboxTabState _state = MailboxTabState.NotLoaded;

    public bool IsLoading => State is MailboxTabState.NotLoaded or MailboxTabState.Loading;
    public bool ShowSections => State == MailboxTabState.Loaded;
    public bool ShowStateMessage => State is MailboxTabState.NeedsConsent or MailboxTabState.NoMailbox or MailboxTabState.Failed;
    public bool ShowGrant => State == MailboxTabState.NeedsConsent;

    [ObservableProperty]
    private string? _stateTitle;

    [ObservableProperty]
    private string? _stateMessage;

    /// <summary>One line above the sections: what kind of mailbox this is and its address.</summary>
    [ObservableProperty]
    private string? _summaryText;

    /// <summary>True while a change is being written. Every action waits for it: one write at a time per mailbox.</summary>
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    public ObservableCollection<MailboxSection> Sections { get; } = [];

    public IReadOnlyList<FolderChoice> FolderChoices { get; }

    [ObservableProperty]
    private FolderChoice _selectedFolder;

    [ObservableProperty]
    private MailboxSection _folderSection = new("Folder permissions", "Nobody has been given access to this folder.");

    partial void OnSelectedFolderChanged(FolderChoice value)
    {
        if (State == MailboxTabState.Loaded) _ = LoadFolderAsync(value, _cts?.Token ?? CancellationToken.None);
    }

    private MailboxRef Target => new(_userId, _detail?.PrimarySmtp ?? _db.GetUser(_userId)?.DisplayName ?? _userId);

    // ---- lifecycle -------------------------------------------------------------------------------------------------

    public void Start(string userId) => _userId = userId;

    /// <summary>Called when the tab is selected. Loads once; F5 on the page resets it.</summary>
    public void EnsureLoaded()
    {
        if (_requested || _userId.Length == 0) return;
        _requested = true;
        _ = LoadAsync(quiet: false);
    }

    public void Reset() => _requested = false;

    [RelayCommand]
    private async Task GrantAsync()
    {
        try
        {
            var result = await _consent.EnsureAsync(Capabilities.ManageMailboxes, CancellationToken.None);
            if (result.CanRun)
            {
                _requested = true;
                await LoadAsync(quiet: false);
            }
        }
        catch (SignInException ex)
        {
            StateMessage = ex.Kind == SignInFailureKind.Cancelled
                ? "Consent was cancelled. Nothing changed."
                : $"Consent did not complete: {ex.Message}";
        }
    }

    /// <param name="quiet">After a write: keep the current sections on screen until the new ones are ready.</param>
    private async Task LoadAsync(bool quiet)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            await LoadCoreAsync(cts, quiet);
        }
        catch (OperationCanceledException)
        {
            // The page closed, or a newer load started.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mailbox tab {UserId}: load failed", _userId);
            State = MailboxTabState.Failed;
            StateTitle = "Couldn't read this mailbox";
            StateMessage = ex.Message;
        }
    }

    private async Task LoadCoreAsync(CancellationTokenSource cts, bool quiet)
    {
        var ct = cts.Token;
        var permission = _permissions.Check(Capabilities.ManageMailboxes);
        if (!_exchange.IsAvailable || permission.NeedsConsent)
        {
            State = MailboxTabState.NeedsConsent;
            StateTitle = "Mailbox details need the Exchange Online permission";
            StateMessage = "This installation hasn't been granted 'Exchange.Manage' yet. Granting it lets the console read and change " +
                           "this mailbox as you; what you can actually do is still decided by your Exchange role.";
            return;
        }

        if (!quiet) State = MailboxTabState.Loading;
        var cached = await Task.Run(() => _db.GetMailbox(_userId), ct);

        var usage = _exchange.GetUsageAsync(_userId, archive: false, ct);
        var fullAccess = _exchange.GetFullAccessAsync(_userId, ct);
        var sendAs = _exchange.GetSendAsAsync(_userId, ct);
        var autoReply = _exchange.GetAutoReplyAsync(_userId, ct);
        var regional = _exchange.GetRegionalAsync(_userId, ct);

        var mailbox = await _exchange.GetMailboxAsync(_userId, ct);
        if (ct.IsCancellationRequested) return;

        if (mailbox.IsNotFound)
        {
            cts.Cancel();
            State = MailboxTabState.NoMailbox;
            var kind = cached?.Kind ?? MailboxKind.None;
            StateTitle = kind == MailboxKind.MailUser ? "The mailbox lives elsewhere" : "No mailbox in this tenant";
            StateMessage = kind == MailboxKind.MailUser
                ? "Exchange Online knows this person as a mail user: their mail is delivered to a mailbox on-prem or in another tenant, so there is nothing here to show."
                : "Exchange Online has no mailbox for this person. A mailbox is created when a licence that includes Exchange is assigned; a shared mailbox is created in Exchange directly.";
            return;
        }

        if (!mailbox.IsSuccess)
        {
            State = MailboxTabState.Failed;
            StateTitle = mailbox.IsForbidden ? "Exchange Online refused" : "Couldn't read this mailbox";
            StateMessage = mailbox.Message;
            _logger.LogWarning("Mailbox tab {UserId}: {Code} {Type}", _userId, mailbox.Error?.ErrorCode, mailbox.Error?.ExceptionName);
            return;
        }

        var m = _detail = mailbox.Value!;
        var names = await _exchange.ResolveRecipientsAsync(
            m.SendOnBehalf.Concat(m.AcceptOnlyFrom).Concat(m.RejectFrom).Concat(m.ForwardingAddress is { } f ? [f] : []), ct);
        _domains ??= await TryDomainsAsync(ct);
        if (ct.IsCancellationRequested) return;

        SummaryText = $"{MailboxKinds.Describe(m.Kind)} · {m.PrimarySmtp}";
        var overview = Section("Mailbox", "");
        var size = Section("Size and retention", "");
        var addresses = Section("Email addresses", "No addresses — which Exchange should never report.");
        var full = new MailboxSection("Full Access", "Nobody else can open this mailbox.")
        {
            ToggleLabel = "Show the mailbox in a new delegate's Outlook automatically",
            ToggleValue = true,
        };
        var sendAsSection = Section("Send As", "Nobody else can send as this mailbox.");
        var onBehalf = Section("Send on Behalf", "Nobody can send on behalf of this mailbox.");
        var delivery = Section("Forwarding and delivery", "");
        var replies = Section("Automatic replies", "");
        var region = Section("Regional settings", "");

        overview.Show(Overview(m));
        addresses.Show(Addresses(m), m.DirSynced ? SyncedReason : null);
        addresses.Actions = m.DirSynced ? [] : [new SectionAction("Add address…", new AsyncRelayCommand(AddAddressAsync))];
        onBehalf.Show(OnBehalf(m, names));
        onBehalf.Actions = m.DirSynced ? [] : [new SectionAction("Add person…", new AsyncRelayCommand(() => PickAndRunAsync(
            "Who may send on behalf of this mailbox?", p => _exchange.ChangeListAsync(Target, MailboxList.SendOnBehalf, p.Id, p.DisplayName ?? p.Id, add: true, CancellationToken.None))))];
        onBehalf.BlockedReason = m.DirSynced ? SyncedReason : null;
        delivery.Show(Delivery(m, names));
        delivery.Actions = m.DirSynced ? [] : DeliveryActions(m);
        delivery.BlockedReason = m.DirSynced ? SyncedReason : null;
        full.Actions = [new SectionAction("Add person…", new AsyncRelayCommand(() => PickAndRunAsync(
            "Who should get Full Access to this mailbox?", p => _exchange.GrantFullAccessAsync(Target, p.UserPrincipalName ?? p.Id, full.ToggleValue, CancellationToken.None))))];
        sendAsSection.Actions = [new SectionAction("Add person…", new AsyncRelayCommand(() => PickAndRunAsync(
            "Who may send as this mailbox?", p => _exchange.GrantSendAsAsync(Target, p.UserPrincipalName ?? p.Id, CancellationToken.None))))];
        replies.Actions = [new SectionAction("Edit automatic replies…", new AsyncRelayCommand(EditAutoReplyAsync))];
        region.Actions = [new SectionAction("Change language or time zone…", new AsyncRelayCommand(EditRegionalAsync))];

        var fresh = new[] { overview, size, addresses, full, sendAsSection, onBehalf, delivery, replies, region };
        Sections.Clear();
        foreach (var s in fresh) Sections.Add(s);
        State = MailboxTabState.Loaded;
        _ = LoadFolderAsync(SelectedFolder, ct);

        var fa = await fullAccess;
        Fill(full, fa, entries => [.. entries.Select(e => new MailboxRow(e.IsDeny ? "Denied" : "Full Access", e.Principal,
            caption: e.Rights.Count > 1 ? string.Join(", ", e.Rights) : null)
        {
            Remove = e.IsDeny ? null : new AsyncRelayCommand(() => RunAsync(t => _exchange.RevokeFullAccessAsync(Target, e.Principal, t))),
        })]);

        var sa = await sendAs;
        Fill(sendAsSection, sa, entries =>
        [
            .. entries.Select(e => new MailboxRow(e.IsDeny ? "Denied" : "Send As", e.Principal)
            {
                Remove = e.IsDeny ? null : new AsyncRelayCommand(() => RunAsync(t => _exchange.RevokeSendAsAsync(Target, e.Principal, t))),
            }),
            Toggle("Copy to this mailbox's Sent Items", m.CopySentAsToSentItems, on => _exchange.SetCopySentAsAsync(Target, on, CancellationToken.None)),
        ]);

        var ar = await autoReply;
        _autoReply = ar.Value;
        Fill(replies, ar, AutoReplies);
        var rg = await regional;
        _regional = rg.Value;
        Fill(region, rg, Regional);

        var usageResult = await usage;
        var archiveResult = m.HasArchive ? await _exchange.GetUsageAsync(_userId, archive: true, ct) : null;
        if (ct.IsCancellationRequested) return;
        if (usageResult.IsSuccess) size.Show(Size(m, usageResult.Value!, archiveResult));
        else size.Fail(usageResult.Message ?? "Couldn't read the mailbox size.");
    }

    private async Task<IReadOnlyList<string>?> TryDomainsAsync(CancellationToken ct)
    {
        try
        {
            return await _exchange.GetAcceptedDomainsAsync(ct);
        }
        catch (ExchangeException ex)
        {
            _logger.LogInformation("Accepted domains unavailable: {Code}", ex.ErrorCode);
            return null;
        }
    }

    private async Task LoadFolderAsync(FolderChoice folder, CancellationToken ct)
    {
        var section = FolderSection = new MailboxSection($"{folder.Label} permissions", $"Only the owner has access to {folder.Label}.");
        var result = await _exchange.GetFolderPermissionsAsync(_userId, folder.FolderType, ct);
        if (ct.IsCancellationRequested || !ReferenceEquals(section, FolderSection)) return;
        if (!result.IsSuccess)
        {
            section.Fail(result.Message ?? "Couldn't read the folder's permissions.");
            return;
        }

        var p = result.Value!;
        var rows = p.Entries.Select(e => new MailboxRow(e.Who, e.Rights.FirstOrDefault() ?? "None", ExchangeService.FolderRoles,
            role => RunAsync(t => _exchange.SetFolderPermissionAsync(Target, folder.FolderType, e.Principal, e.Principal, role, t)),
            caption: e.SharingFlags.Count > 0 ? "Delegate flags: " + string.Join(", ", e.SharingFlags) : null)
        {
            Remove = e.IsDefault || e.IsAnonymous ? null
                : new AsyncRelayCommand(() => RunAsync(t => _exchange.SetFolderPermissionAsync(Target, folder.FolderType, e.Principal, e.Principal, null, t))),
        }).ToList();
        section.Show(rows, p.Folder.Name == folder.Label ? null : $"In this mailbox's language the folder is called \"{p.Folder.Name}\".");
        section.Actions = [new SectionAction("Add person…", new AsyncRelayCommand(() => PickAndRunAsync(
            $"Who should get access to {folder.Label}?",
            person => _exchange.SetFolderPermissionAsync(Target, folder.FolderType, person.Id, person.DisplayName ?? person.Id, "Reviewer", CancellationToken.None),
            after: $"They start as Reviewer (can read). Change the role in the list.")))];
    }

    // ---- section builders ------------------------------------------------------------------------------------------

    private List<MailboxRow> Overview(MailboxDetail m)
    {
        var convertible = m.Kind is MailboxKind.User or MailboxKind.Shared;
        var rows = new List<MailboxRow>
        {
            new("Type", MailboxKinds.Describe(m.Kind))
            {
                ActionLabel = convertible ? m.Kind == MailboxKind.Shared ? "Convert to user mailbox…" : "Convert to shared…" : null,
                Action = convertible ? new AsyncRelayCommand(() => ConvertAsync(m)) : null,
            },
            new("Primary address", m.PrimarySmtp ?? "—", isMono: true),
            new("Alias", m.Alias ?? "—", isMono: true),
            new("Created", TimeText.Both(m.Created)),
            new("Address lists", m.HiddenFromAddressLists ? "Hidden" : "Visible",
                caption: m.HiddenFromAddressLists ? "People can't find it in the address book." : null)
            {
                ActionLabel = m.DirSynced ? null : m.HiddenFromAddressLists ? "Show" : "Hide",
                Action = m.DirSynced ? null : new AsyncRelayCommand(() => RunAsync(t => _exchange.SetHiddenAsync(Target, !m.HiddenFromAddressLists, t))),
                ActionBlockedReason = m.DirSynced ? "Mastered in on-prem AD" : null,
            },
        };
        if (m.DirSynced) rows.Add(new("Source", "Synced from on-prem AD", caption: "Some settings are mastered in AD (doc 13 §3)."));
        return rows;
    }

    private List<MailboxRow> Addresses(MailboxDetail m) =>
    [
        .. m.EmailAddresses.Select(a => new MailboxRow(a.Kind, a.Address, isMono: true)
        {
            ActionLabel = !m.DirSynced && a.IsSmtp && !a.IsPrimary ? "Make primary" : null,
            Action = !m.DirSynced && a.IsSmtp && !a.IsPrimary ? new AsyncRelayCommand(() => RunAsync(t => _exchange.SetPrimaryAddressAsync(Target, a.Address, t))) : null,
            Remove = !m.DirSynced && a.IsSmtp && !a.IsPrimary ? new AsyncRelayCommand(() => RunAsync(t => _exchange.RemoveAddressAsync(Target, a.Address, t))) : null,
        }),
    ];

    private List<MailboxRow> OnBehalf(MailboxDetail m, IReadOnlyDictionary<string, RecipientRef> names)
    {
        var rows = m.SendOnBehalf.Select(n => names.TryGetValue(n, out var r) ? r : RecipientRef.Unresolved(n))
            .Select(r => new MailboxRow("Can send on behalf", r.Display)
            {
                Remove = m.DirSynced ? null : new AsyncRelayCommand(() => RunAsync(t =>
                    _exchange.ChangeListAsync(Target, MailboxList.SendOnBehalf, r.Identity, r.DisplayName ?? r.ExchangeName, add: false, t))),
            }).ToList();
        if (rows.Count > 0 || m.CopySendOnBehalfToSentItems)
        {
            rows.Add(Toggle("Copy to this mailbox's Sent Items", m.CopySendOnBehalfToSentItems, on => _exchange.SetCopySendOnBehalfAsync(Target, on, CancellationToken.None)));
        }

        return rows;
    }

    private List<MailboxRow> Delivery(MailboxDetail m, IReadOnlyDictionary<string, RecipientRef> names)
    {
        var rows = new List<MailboxRow>();
        if (!m.IsForwarding) rows.Add(new("Forwarding", "Off"));
        else
        {
            var to = m.ForwardingAddress is { } name
                ? (names.TryGetValue(name, out var r) ? r.Display : name)
                : m.ForwardingSmtpAddress!.Replace("smtp:", string.Empty, StringComparison.OrdinalIgnoreCase);
            rows.Add(new("Forwarding to", to, isMono: m.ForwardingAddress is null,
                caption: m.ForwardingAddress is null ? "An external address" : null));
            var internalTarget = m.ForwardingAddress is { } n2 && names.TryGetValue(n2, out var r2) ? r2.Identity : m.ForwardingAddress;
            var external = m.ForwardingAddress is null ? m.ForwardingSmtpAddress?.Replace("smtp:", string.Empty, StringComparison.OrdinalIgnoreCase) : null;
            rows.Add(new("Keep a copy here", m.DeliverToMailboxAndForward ? "Yes" : "No — mail only goes to the forwarding address")
            {
                ActionLabel = m.DirSynced ? null : m.DeliverToMailboxAndForward ? "Stop keeping a copy" : "Keep a copy",
                Action = m.DirSynced ? null : new AsyncRelayCommand(() => RunAsync(t =>
                    _exchange.SetForwardingAsync(Target, internalTarget, external, !m.DeliverToMailboxAndForward, to, t))),
            });
        }

        if (m.AcceptOnlyFrom.Count == 0) rows.Add(new("Accept mail only from", "Anyone"));
        foreach (var n in m.AcceptOnlyFrom)
        {
            var r = names.TryGetValue(n, out var found) ? found : RecipientRef.Unresolved(n);
            rows.Add(new("Accept mail only from", r.Display)
            {
                Remove = m.DirSynced ? null : new AsyncRelayCommand(() => RunAsync(t =>
                    _exchange.ChangeListAsync(Target, MailboxList.AcceptOnlyFrom, r.Identity, r.DisplayName ?? r.ExchangeName, add: false, t))),
            });
        }

        foreach (var n in m.RejectFrom)
        {
            var r = names.TryGetValue(n, out var found) ? found : RecipientRef.Unresolved(n);
            rows.Add(new("Reject mail from", r.Display)
            {
                Remove = m.DirSynced ? null : new AsyncRelayCommand(() => RunAsync(t =>
                    _exchange.ChangeListAsync(Target, MailboxList.RejectFrom, r.Identity, r.DisplayName ?? r.ExchangeName, add: false, t))),
            });
        }

        rows.Add(Toggle("Mail from outside the organisation", !m.RequireSenderAuthentication,
            accept => _exchange.SetRequireSenderAuthenticationAsync(Target, !accept, CancellationToken.None), on: "Accepted", off: "Rejected"));
        return rows;
    }

    private IReadOnlyList<SectionAction> DeliveryActions(MailboxDetail m)
    {
        var actions = new List<SectionAction>
        {
            new("Forward to a person…", new AsyncRelayCommand(() => PickAndRunAsync("Forward this mailbox's mail to whom?",
                p => _exchange.SetForwardingAsync(Target, p.Id, null, keepCopy: true, p.DisplayName, CancellationToken.None),
                after: "A copy is kept here too; change that in the list."))),
            new("Forward outside…", new AsyncRelayCommand(ForwardExternallyAsync)),
        };
        if (m.IsForwarding)
        {
            actions.Add(new("Stop forwarding", new AsyncRelayCommand(() => RunAsync(t => _exchange.SetForwardingAsync(Target, null, null, false, null, t)))));
        }

        actions.Add(new("Accept mail only from a person…", new AsyncRelayCommand(() => PickAndRunAsync(
            "Accept mail only from whom? Everyone else will be rejected.",
            p => _exchange.ChangeListAsync(Target, MailboxList.AcceptOnlyFrom, p.Id, p.DisplayName ?? p.Id, add: true, CancellationToken.None)))));
        actions.Add(new("Reject mail from a person…", new AsyncRelayCommand(() => PickAndRunAsync(
            "Reject mail from whom?",
            p => _exchange.ChangeListAsync(Target, MailboxList.RejectFrom, p.Id, p.DisplayName ?? p.Id, add: true, CancellationToken.None)))));
        return actions;
    }

    private List<MailboxRow> Size(MailboxDetail m, MailboxUsage usage, ExchangeSection<MailboxUsage>? archive)
    {
        var limit = m.ProhibitSendReceiveQuota;
        var used = usage.TotalItemSize;
        var fraction = used.FractionOf(limit);
        var rows = new List<MailboxRow>
        {
            new("Used", $"{used.ToDisplay()} of {limit.ToDisplay()}" + (fraction is { } f ? $" ({f.ToString("P1", CultureInfo.CurrentCulture)})" : string.Empty)),
            new("Items", usage.ItemCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "—",
                caption: usage.DeletedItemSize.Bytes is > 0 ? $"Plus {usage.DeletedItemSize.ToDisplay()} in Deleted Items" : null),
            new("Last used by a person", TimeText.Both(usage.LastUserAction),
                caption: "Background syncs from Outlook and phones don't count."),
        };

        if (!m.HasArchive)
        {
            rows.Add(new("Archive", "Not enabled") { ActionLabel = "Enable archive…", Action = new AsyncRelayCommand(() => EnableArchiveAsync(m)) });
        }
        else
        {
            var text = archive is { IsSuccess: true, Value: { } a } ? $"{a.TotalItemSize.ToDisplay()} of {m.ArchiveQuota.ToDisplay()}" : "Enabled";
            rows.Add(new("Archive", text, caption: m.AutoExpandingArchive ? "Auto-expanding" : archive?.Message)
            {
                ActionLabel = m.AutoExpandingArchive ? null : "Make auto-expanding…",
                Action = m.AutoExpandingArchive ? null : new AsyncRelayCommand(() => AutoExpandAsync(m)),
            });
        }

        rows.Add(m.LitigationHold
            ? new("Litigation hold", $"On · {(m.LitigationHoldDuration is "Unlimited" or null ? "kept indefinitely" : $"{m.LitigationHoldDuration} days")}",
                caption: m.LitigationHoldSince is { } since ? $"Since {TimeText.Absolute(since)}" + (m.LitigationHoldOwner is { } o ? $" by {o}" : string.Empty) : null)
            {
                ActionLabel = "Take off hold…",
                Action = new AsyncRelayCommand(() => ReleaseHoldAsync()),
            }
            : new("Litigation hold", "Off", caption: "Needs Exchange Online Plan 2 or the archiving add-on.")
            {
                ActionLabel = "Put on hold…",
                Action = new AsyncRelayCommand(PlaceHoldAsync),
            });
        return rows;
    }

    private static IReadOnlyList<MailboxRow> AutoReplies(AutoReplySettings a)
    {
        if (!a.IsOn) return [new("State", "Off")];
        var rows = new List<MailboxRow> { new("State", a.IsScheduled ? "Scheduled" : "On") };
        if (a.IsScheduled) rows.Add(new("Window", $"{TimeText.Absolute(a.Start)} → {TimeText.Absolute(a.End)}"));
        rows.Add(new("Inside the organisation", AutoReplySettings.PlainText(a.InternalMessage) ?? "(no message)"));
        rows.Add(new("Outside it", a.ExternalAudience == "None" ? "No reply" : AutoReplySettings.PlainText(a.ExternalMessage) ?? "(no message)",
            caption: a.ExternalAudience == "Known" ? "Only to people in their contacts" : null));
        return rows;
    }

    private static IReadOnlyList<MailboxRow> Regional(RegionalSettings r) => !r.IsSet
        ? [new("Language", "Not set yet", caption: "Set the first time the mailbox is opened in Outlook on the web, or here.")]
        :
        [
            new("Language", r.Language ?? "—"),
            new("Time zone", r.TimeZone ?? "—"),
            new("Date and time", $"{r.DateFormat ?? "—"} · {r.TimeFormat ?? "—"}"),
            new("Folder names", r.FolderNamesFollowLanguage ? "In the mailbox's language" : "Unchanged since the mailbox was created"),
        ];

    private MailboxRow Toggle(string label, bool value, Func<bool, Task<WriteOutcome>> set, string on = "On", string off = "Off") =>
        new(label, value ? on : off)
        {
            ActionLabel = value ? (on == "On" ? "Turn off" : off == "Rejected" ? "Reject" : "Turn off") : (on == "On" ? "Turn on" : "Accept"),
            Action = new AsyncRelayCommand(() => RunAsync(_ => set(!value))),
        };

    private static MailboxSection Section(string title, string empty) => new(title, empty);

    private static void Fill<T>(MailboxSection section, ExchangeSection<T> result, Func<T, IReadOnlyList<MailboxRow>> rows, string? note = null)
    {
        if (result.IsSuccess) section.Show(rows(result.Value!), note);
        else section.Fail(result.Message ?? "Couldn't read this section.");
    }

    // ---- actions that ask first ------------------------------------------------------------------------------------

    private async Task AddAddressAsync()
    {
        var domains = _domains is { Count: > 0 } d ? $"This tenant accepts mail for {string.Join(", ", d)}." : "Use one of the tenant's own domains.";
        var prompt = new TextPromptDialogViewModel("Add an email address", "Address", _domains is { Count: > 0 } ds ? $"name@{ds[0]}" : "name@domain")
        {
            Message = $"{domains} Mail sent to the new address arrives in this mailbox. Exchange refuses an address that is already somebody else's and says whose.",
        };
        if (!await _dialogs.ShowAsync(prompt)) return;
        var address = prompt.Value.Trim();
        if (!address.Contains('@', StringComparison.Ordinal) || address.StartsWith('@') || address.EndsWith('@'))
        {
            _toasts.Show($"\"{address}\" isn't an email address.");
            return;
        }

        await RunAsync(t => _exchange.AddAddressAsync(Target, address, t));
    }

    private async Task ForwardExternallyAsync()
    {
        var prompt = new TextPromptDialogViewModel("Forward outside the organisation", "Address", "someone@elsewhere.com")
        {
            Message = "Every message this mailbox receives will also be sent to this address, and a copy kept here. Many tenants block automatic " +
                      "forwarding outside the organisation; if this one does, Exchange accepts the setting but the mail is not delivered.",
        };
        if (!await _dialogs.ShowAsync(prompt)) return;
        var address = prompt.Value.Trim();
        if (!address.Contains('@', StringComparison.Ordinal))
        {
            _toasts.Show($"\"{address}\" isn't an email address.");
            return;
        }

        await RunAsync(t => _exchange.SetForwardingAsync(Target, null, address, keepCopy: true, address, t));
    }

    private async Task ConvertAsync(MailboxDetail m)
    {
        var toShared = m.Kind != MailboxKind.Shared;
        var confirm = new ConfirmDialogViewModel
        {
            Heading = toShared ? "Convert to a shared mailbox?" : "Convert to a user mailbox?",
            ConfirmLabel = toShared ? "Convert to shared" : "Convert to user mailbox",
            Message = toShared
                ? "Nothing in the mailbox is lost. A shared mailbox needs no licence up to 50 GB, but converting does not remove licences — " +
                  "remove them separately afterwards (offboarding does it in the right order). People who should use it need Full Access."
                : "Nothing in the mailbox is lost. A user mailbox needs a licence that includes Exchange; without one, it is deleted 30 days " +
                  "from now. The account behind it must also be able to sign in for anyone to use it.",
        };
        if (!await _dialogs.ShowAsync(confirm)) return;
        await RunAsync(t => _exchange.ConvertAsync(Target, toShared, t));
    }

    private async Task EnableArchiveAsync(MailboxDetail m)
    {
        var confirm = new ConfirmDialogViewModel
        {
            Heading = "Enable the archive?",
            ConfirmLabel = "Enable archive",
            Message = "An archive gives this mailbox a second store for older mail. It can't be undone from here: disabling an archive later " +
                      "starts a 30-day countdown to deleting everything in it. Licensing: user mailboxes need Exchange Online Plan 2 or the " +
                      "archiving add-on for it to be within terms.",
        };
        if (!await _dialogs.ShowAsync(confirm)) return;
        await RunAsync(t => _exchange.EnableArchiveAsync(Target, t));
    }

    private async Task AutoExpandAsync(MailboxDetail m)
    {
        var confirm = new ConfirmDialogViewModel
        {
            Heading = "Make the archive auto-expanding?",
            ConfirmLabel = "Make auto-expanding",
            Destructive = true,
            Message = "The archive will grow past its quota (up to 1.5 TB) by adding storage as it fills. Exchange offers no way to turn this off once it is on.",
        };
        if (!await _dialogs.ShowAsync(confirm)) return;
        await RunAsync(t => _exchange.EnableAutoExpandingArchiveAsync(Target, t));
    }

    private async Task PlaceHoldAsync()
    {
        var prompt = new TextPromptDialogViewModel("Put on litigation hold", "Hold for how many days? Type 'indefinitely' to keep everything until the hold is removed.", "indefinitely")
        {
            Message = "Nothing in the mailbox can be permanently deleted while it is held — including by its owner. It needs Exchange Online " +
                      "Plan 2 or the archiving add-on; without one, Exchange refuses and says so.",
        };
        if (!await _dialogs.ShowAsync(prompt)) return;
        var text = prompt.Value.Trim();
        int? days = null;
        if (!text.StartsWith("indef", StringComparison.OrdinalIgnoreCase) && !text.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) || n <= 0)
            {
                _toasts.Show($"\"{text}\" isn't a number of days. Type a number, or 'indefinitely'.");
                return;
            }

            days = n;
        }

        await RunAsync(t => _exchange.SetLitigationHoldAsync(Target, true, days, t));
    }

    private async Task ReleaseHoldAsync()
    {
        var confirm = new ConfirmDialogViewModel
        {
            Heading = "Take the mailbox off litigation hold?",
            ConfirmLabel = "Take off hold",
            Destructive = true,
            Message = "Items deleted while it was held become deletable again under the mailbox's normal retention. Check with whoever asked for the hold first.",
        };
        if (!await _dialogs.ShowAsync(confirm)) return;
        await RunAsync(t => _exchange.SetLitigationHoldAsync(Target, false, null, t));
    }

    private async Task EditAutoReplyAsync()
    {
        var dialog = new AutoReplyDialogViewModel(Target.Label, _autoReply ?? new AutoReplySettings());
        if (!await _dialogs.ShowAsync(dialog)) return;
        await RunAsync(t => _exchange.SetAutoReplyAsync(Target, dialog.ToSettings(), t));
    }

    private async Task EditRegionalAsync()
    {
        var dialog = new RegionalDialogViewModel(Target.Label, _regional ?? new RegionalSettings());
        if (!await _dialogs.ShowAsync(dialog) || dialog.Language is null || dialog.TimeZone is null) return;
        await RunAsync(t => _exchange.SetRegionalAsync(Target, dialog.Language.Value, dialog.TimeZone.Value, dialog.LocalizeFolders, t));
    }

    private Task PickAndRunAsync(string prompt, Func<PersonRef, Task<WriteOutcome>> write, string? after = null)
    {
        if (PickPerson is null)
        {
            _toasts.Show("The person picker isn't available here.");
            return Task.CompletedTask;
        }

        PickPerson(prompt, person => _ = RunAsync(async _ =>
        {
            var outcome = await write(person);
            return after is not null && outcome.Success && !outcome.WasNoOp ? outcome with { Summary = $"{outcome.Summary}. {after}" } : outcome;
        }));
        return Task.CompletedTask;
    }

    // ---- the one write path ----------------------------------------------------------------------------------------

    private async Task RunAsync(Func<CancellationToken, Task<WriteOutcome>> write)
    {
        if (IsBusy)
        {
            _toasts.Show("Still saving the last change to this mailbox — try again in a moment.");
            return;
        }

        var permission = await _consent.EnsureAsync(Capabilities.ManageMailboxes, CancellationToken.None);
        if (!permission.CanRun) return;

        IsBusy = true;
        var settle = _exchange.TimeUntilSettled(_userId);
        BusyText = settle > TimeSpan.Zero
            ? $"Saving… Exchange needs a few seconds between changes to one mailbox (about {Math.Ceiling(settle.TotalSeconds):F0} s)."
            : "Saving…";
        WriteOutcome outcome;
        try
        {
            outcome = await write(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mailbox write on {UserId} failed unexpectedly", _userId);
            _toasts.Show($"That didn't work: {ex.Message}");
            return;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }

        if (!outcome.Success)
        {
            _toasts.Show(outcome.FriendlyError ?? outcome.Summary);
            return;
        }

        var text = outcome.Warning is null ? outcome.Summary : $"{outcome.Summary}. Exchange said: {outcome.Warning}";
        _toasts.Show(text, outcome.CanUndo ? "Undo" : null, outcome.CanUndo ? () => UndoAsync(outcome) : null);
        if (!outcome.WasNoOp) await LoadAsync(quiet: true);
    }

    private async Task UndoAsync(WriteOutcome outcome)
    {
        await RunAsync(async t =>
        {
            var back = await outcome.Undo!(t);
            return back.Success ? back with { Summary = $"Undone: {back.Summary}", Undo = null } : back;
        });
    }

    public void Dispose() => _cts?.Cancel();
}
