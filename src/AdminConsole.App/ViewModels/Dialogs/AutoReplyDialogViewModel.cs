using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Exchange;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// Edits a mailbox's automatic replies (M14.4). Messages are typed as plain text and sent as HTML paragraphs, because a
/// plain-text box is what an admin writing an out-of-office expects and Outlook stores HTML.
/// </summary>
public sealed partial class AutoReplyDialogViewModel : DialogViewModelBase
{
    public static readonly IReadOnlyList<ChoiceItem> States =
    [
        new("Disabled", "Off"),
        new("Enabled", "On until turned off"),
        new("Scheduled", "On between two dates"),
    ];

    public static readonly IReadOnlyList<ChoiceItem> Audiences =
    [
        new("None", "Don't reply outside the organisation"),
        new("Known", "Reply only to people in their contacts"),
        new("All", "Reply to everyone outside"),
    ];

    public AutoReplyDialogViewModel(string mailboxLabel, AutoReplySettings current)
    {
        MailboxLabel = mailboxLabel;
        _state = States.FirstOrDefault(s => s.Value.Equals(current.State, StringComparison.OrdinalIgnoreCase)) ?? States[0];
        _audience = Audiences.FirstOrDefault(a => a.Value.Equals(current.ExternalAudience, StringComparison.OrdinalIgnoreCase)) ?? Audiences[0];
        _internalText = AutoReplySettings.PlainText(current.InternalMessage) ?? string.Empty;
        _externalText = AutoReplySettings.PlainText(current.ExternalMessage) ?? string.Empty;
        _startDate = (current.Start ?? DateTimeOffset.Now).LocalDateTime.Date;
        _endDate = (current.End ?? DateTimeOffset.Now.AddDays(7)).LocalDateTime.Date;
    }

    public string MailboxLabel { get; }

    public override string Title => $"Automatic replies — {MailboxLabel}";

    public override string ConfirmText => "Save";

    public override double Width => 620;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScheduled))]
    [NotifyPropertyChangedFor(nameof(IsOn))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(Problem))]
    private ChoiceItem _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepliesOutside))]
    private ChoiceItem _audience;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(Problem))]
    private string _internalText;

    [ObservableProperty]
    private string _externalText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(Problem))]
    private DateTime? _startDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(Problem))]
    private DateTime? _endDate;

    public bool IsOn => State.Value != "Disabled";
    public bool IsScheduled => State.Value == "Scheduled";
    public bool RepliesOutside => Audience.Value != "None";

    /// <summary>Why Save is disabled, in words — never a silently dead button.</summary>
    public string? Problem =>
        IsOn && string.IsNullOrWhiteSpace(InternalText) ? "Write the reply people inside the organisation will get."
        : IsScheduled && (StartDate is null || EndDate is null) ? "Choose both dates."
        : IsScheduled && EndDate < StartDate ? "The end date is before the start date."
        : null;

    public override bool CanConfirm => Problem is null;

    /// <summary>The settings to send. The schedule runs from the start of the first day to the end of the last.</summary>
    public AutoReplySettings ToSettings() => new()
    {
        State = State.Value,
        InternalMessage = Html(InternalText),
        ExternalMessage = Html(ExternalText),
        ExternalAudience = Audience.Value,
        Start = IsScheduled && StartDate is { } s ? new DateTimeOffset(s.Date) : null,
        End = IsScheduled && EndDate is { } e ? new DateTimeOffset(e.Date.AddDays(1).AddMinutes(-1)) : null,
    };

    private static string? Html(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : string.Concat(text.Replace("\r\n", "\n").Split("\n\n").Select(p => $"<p>{WebUtility.HtmlEncode(p.Trim()).Replace("\n", "<br>")}</p>"));
}
