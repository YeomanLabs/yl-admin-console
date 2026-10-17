using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AdminConsole.App.Services;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// User 360's Mailbox tab (M13.5, doc 13 §2), read-only. Loads the first time the tab is selected rather than with the
/// page, because Exchange calls cost hundreds of milliseconds each and most visits to a person never open it. Sections
/// load in parallel and fail on their own.
/// </summary>
public sealed partial class MailboxTabViewModel : ObservableObject, IDisposable
{
    private readonly ExchangeService _exchange;
    private readonly ConsentService _consent;
    private readonly PermissionChecker _permissions;
    private readonly CacheDb _db;
    private readonly ILogger<MailboxTabViewModel> _logger;
    private string _userId = string.Empty;
    private bool _requested;
    private CancellationTokenSource? _cts;

    public MailboxTabViewModel(ExchangeService exchange, ConsentService consent, PermissionChecker permissions, CacheDb db, ILogger<MailboxTabViewModel> logger)
    {
        _exchange = exchange;
        _consent = consent;
        _permissions = permissions;
        _db = db;
        _logger = logger;
        FolderChoices = [.. MailboxFolder.Delegable.Select(f => new FolderChoice(f.FolderType, f.Label))];
        _selectedFolder = FolderChoices[0];
    }

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

    // ---- lifecycle -------------------------------------------------------------------------------------------------

    public void Start(string userId) => _userId = userId;

    /// <summary>Called when the tab is selected. Loads once; F5 on the page resets it.</summary>
    public void EnsureLoaded()
    {
        if (_requested || _userId.Length == 0) return;
        _requested = true;
        _ = LoadAsync();
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
                await LoadAsync();
            }
        }
        catch (SignInException ex)
        {
            StateMessage = ex.Kind == SignInFailureKind.Cancelled
                ? "Consent was cancelled. Nothing changed."
                : $"Consent did not complete: {ex.Message}";
        }
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            await LoadCoreAsync(cts);
        }
        catch (OperationCanceledException)
        {
            // The page closed, or F5 started a newer load.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mailbox tab {UserId}: load failed", _userId);
            State = MailboxTabState.Failed;
            StateTitle = "Couldn't read this mailbox";
            StateMessage = ex.Message;
        }
    }

    private async Task LoadCoreAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;

        var permission = _permissions.Check(Capabilities.ManageMailboxes);
        if (!_exchange.IsAvailable || permission.NeedsConsent)
        {
            State = MailboxTabState.NeedsConsent;
            StateTitle = "Mailbox details need the Exchange Online permission";
            StateMessage = "This installation hasn't been granted 'Exchange.Manage' yet. Granting it lets the console read " +
                           "this mailbox as you; what you can actually see and change is still decided by your Exchange role.";
            return;
        }

        State = MailboxTabState.Loading;
        var cached = await Task.Run(() => _db.GetMailbox(_userId), ct);

        // Everything that does not depend on Get-Mailbox starts now, in parallel with it.
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

        var m = mailbox.Value!;
        SummaryText = $"{MailboxKinds.Describe(m.Kind)} · {m.PrimarySmtp}";
        var overview = Section("Mailbox", "");
        var size = Section("Size and retention", "");
        var addresses = Section("Email addresses", "No addresses — which Exchange should never report.");
        var full = Section("Full Access", "Nobody else can open this mailbox.");
        var sendAsSection = Section("Send As", "Nobody else can send as this mailbox.");
        var onBehalf = Section("Send on Behalf", "Nobody can send on behalf of this mailbox.");
        var delivery = Section("Forwarding and delivery", "");
        var replies = Section("Automatic replies", "");
        var region = Section("Regional settings", "");
        Sections.Clear();
        foreach (var s in new[] { overview, size, addresses, full, sendAsSection, onBehalf, delivery, replies, region }) Sections.Add(s);
        State = MailboxTabState.Loaded;

        overview.Show(Overview(m));
        addresses.Show([.. m.EmailAddresses.Select(a => new FactRow(a.Kind, a.Address, IsMono: true))],
            m.DirSynced ? "Synced from on-prem AD: addresses are changed there and synced up (doc 13 §3)." : null);
        onBehalf.Show([.. m.SendOnBehalf.Select(p => new FactRow("Can send on behalf", p))],
            CopyNote(m.CopySendOnBehalfToSentItems, "on behalf"));
        delivery.Show(Delivery(m));
        _ = LoadFolderAsync(SelectedFolder, ct);

        Fill(full, await fullAccess, entries => [.. entries.Select(e => new FactRow(e.IsDeny ? "Denied" : "Full Access", e.Principal,
            Caption: e.Rights.Count > 1 ? string.Join(", ", e.Rights) : null))]);
        Fill(sendAsSection, await sendAs, entries => [.. entries.Select(e => new FactRow(e.IsDeny ? "Denied" : "Send As", e.Principal))],
            CopyNote(m.CopySentAsToSentItems, "as"));
        Fill(replies, await autoReply, AutoReplies);
        Fill(region, await regional, Regional);

        var usageResult = await usage;
        var archiveResult = m.HasArchive ? await _exchange.GetUsageAsync(_userId, archive: true, ct) : null;
        if (ct.IsCancellationRequested) return;
        if (usageResult.IsSuccess) size.Show(Size(m, usageResult.Value!, archiveResult));
        else size.Fail(usageResult.Message ?? "Couldn't read the mailbox size.");
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
        var rows = p.Entries.Select(e => new FactRow(e.Who, e.Rights.Count == 0 ? "None" : string.Join(", ", e.Rights),
            Caption: e.SharingFlags.Count > 0 ? "Delegate flags: " + string.Join(", ", e.SharingFlags) : null)).ToList();
        section.Show(rows, p.Folder.Name == folder.Label ? null : $"In this mailbox's language the folder is called \"{p.Folder.Name}\".");
    }

    // ---- section builders ------------------------------------------------------------------------------------------

    private static List<FactRow> Overview(MailboxDetail m)
    {
        var rows = new List<FactRow>
        {
            new("Type", MailboxKinds.Describe(m.Kind)),
            new("Primary address", m.PrimarySmtp ?? "—", IsMono: true),
            new("Alias", m.Alias ?? "—", IsMono: true),
            new("Created", TimeText.Both(m.Created)),
            new("Address lists", m.HiddenFromAddressLists ? "Hidden" : "Visible",
                Caption: m.HiddenFromAddressLists ? "People can't find it in the address book." : null),
        };
        if (m.DirSynced) rows.Add(new("Source", "Synced from on-prem AD", Caption: "Some settings are mastered in AD (doc 13 §3)."));
        return rows;
    }

    private static List<FactRow> Size(MailboxDetail m, MailboxUsage usage, ExchangeSection<MailboxUsage>? archive)
    {
        var limit = m.ProhibitSendReceiveQuota;
        var used = usage.TotalItemSize;
        var fraction = used.FractionOf(limit);
        var rows = new List<FactRow>
        {
            new("Used", $"{used.ToDisplay()} of {limit.ToDisplay()}" + (fraction is { } f ? $" ({f.ToString("P1", CultureInfo.CurrentCulture)})" : string.Empty)),
            new("Items", usage.ItemCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "—",
                Caption: usage.DeletedItemSize.Bytes is > 0 ? $"Plus {usage.DeletedItemSize.ToDisplay()} in Deleted Items" : null),
            new("Last used by a person", TimeText.Both(usage.LastUserAction),
                Caption: "Background syncs from Outlook and phones don't count."),
        };

        if (!m.HasArchive) rows.Add(new("Archive", "Not enabled"));
        else if (archive is { IsSuccess: true, Value: { } a })
        {
            rows.Add(new("Archive", $"{a.TotalItemSize.ToDisplay()} of {m.ArchiveQuota.ToDisplay()}",
                Caption: m.AutoExpandingArchive ? "Auto-expanding" : null));
        }
        else rows.Add(new("Archive", "Enabled", Caption: archive?.Message));

        rows.Add(m.LitigationHold
            ? new("Litigation hold", $"On · {(m.LitigationHoldDuration is "Unlimited" or null ? "kept indefinitely" : $"{m.LitigationHoldDuration} days")}",
                Caption: m.LitigationHoldSince is { } since ? $"Since {TimeText.Absolute(since)}" + (m.LitigationHoldOwner is { } o ? $" by {o}" : string.Empty) : null)
            : new("Litigation hold", "Off"));
        return rows;
    }

    private static List<FactRow> Delivery(MailboxDetail m)
    {
        var rows = new List<FactRow>();
        if (!m.IsForwarding) rows.Add(new("Forwarding", "Off"));
        else
        {
            var to = m.ForwardingAddress ?? m.ForwardingSmtpAddress!.Replace("smtp:", string.Empty, StringComparison.OrdinalIgnoreCase);
            rows.Add(new("Forwarding to", to, IsMono: m.ForwardingAddress is null,
                Caption: m.ForwardingSmtpAddress is not null && m.ForwardingAddress is null ? "An external address" : null));
            rows.Add(new("Keep a copy here", m.DeliverToMailboxAndForward ? "Yes" : "No — mail only goes to the forwarding address"));
        }

        rows.Add(new("Accept mail only from", m.AcceptOnlyFrom.Count == 0 ? "Anyone" : string.Join(", ", m.AcceptOnlyFrom)));
        if (m.RejectFrom.Count > 0) rows.Add(new("Reject mail from", string.Join(", ", m.RejectFrom)));
        rows.Add(new("Senders outside the organisation", m.RequireSenderAuthentication ? "Rejected" : "Accepted"));
        return rows;
    }

    private static IReadOnlyList<FactRow> AutoReplies(AutoReplySettings a)
    {
        if (!a.IsOn) return [new("State", "Off")];
        var rows = new List<FactRow>
        {
            new("State", a.IsScheduled ? "Scheduled" : "On"),
        };
        if (a.IsScheduled) rows.Add(new("Window", $"{TimeText.Absolute(a.Start)} → {TimeText.Absolute(a.End)}"));
        rows.Add(new("Inside the organisation", AutoReplySettings.PlainText(a.InternalMessage) ?? "(no message)"));
        rows.Add(new("Outside it", a.ExternalAudience switch
        {
            "None" => "No reply",
            "Known" => AutoReplySettings.PlainText(a.ExternalMessage) ?? "(no message)",
            _ => AutoReplySettings.PlainText(a.ExternalMessage) ?? "(no message)",
        }, Caption: a.ExternalAudience == "Known" ? "Only to people in their contacts" : null));
        return rows;
    }

    private static IReadOnlyList<FactRow> Regional(RegionalSettings r) => !r.IsSet
        ? [new("Language", "Not set yet", Caption: "Set the first time the mailbox is opened in Outlook on the web.")]
        :
        [
            new("Language", r.Language ?? "—"),
            new("Time zone", r.TimeZone ?? "—"),
            new("Date and time", $"{r.DateFormat ?? "—"} · {r.TimeFormat ?? "—"}"),
            new("Folder names", r.FolderNamesFollowLanguage ? "In the mailbox's language" : "Unchanged since the mailbox was created"),
        ];

    private static string? CopyNote(bool copies, string how) =>
        copies ? $"Mail sent {how} this mailbox is also saved in its Sent Items." : null;

    private static MailboxSection Section(string title, string empty) => new(title, empty);

    private static void Fill<T>(MailboxSection section, ExchangeSection<T> result, Func<T, IReadOnlyList<FactRow>> rows, string? note = null)
    {
        if (result.IsSuccess) section.Show(rows(result.Value!), note);
        else section.Fail(result.Message ?? "Couldn't read this section.");
    }

    public void Dispose() => _cts?.Cancel();
}
