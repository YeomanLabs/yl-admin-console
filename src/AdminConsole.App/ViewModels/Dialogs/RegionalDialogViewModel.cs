using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Exchange;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// A mailbox's language and time zone (M14.2). Exchange takes Windows time zone ids (<c>Central Standard Time</c>) and
/// culture names (<c>en-GB</c>), which is exactly what this machine's own lists hold.
/// </summary>
public sealed partial class RegionalDialogViewModel : DialogViewModelBase
{
    public RegionalDialogViewModel(string mailboxLabel, RegionalSettings current)
    {
        MailboxLabel = mailboxLabel;
        Languages =
        [
            .. CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                .Where(c => c.Name.Contains('-', StringComparison.Ordinal))
                .OrderBy(c => c.EnglishName, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new ChoiceItem(c.Name, $"{c.EnglishName} ({c.Name})")),
        ];
        TimeZones = [.. TimeZoneInfo.GetSystemTimeZones().Select(z => new ChoiceItem(z.Id, z.DisplayName))];
        _language = Languages.FirstOrDefault(l => l.Value.Equals(current.Language, StringComparison.OrdinalIgnoreCase))
                    ?? Languages.FirstOrDefault(l => l.Value.Equals(CultureInfo.CurrentCulture.Name, StringComparison.OrdinalIgnoreCase));
        _timeZone = TimeZones.FirstOrDefault(z => z.Value.Equals(current.TimeZone, StringComparison.OrdinalIgnoreCase))
                    ?? TimeZones.FirstOrDefault(z => z.Value == TimeZoneInfo.Local.Id);
    }

    public string MailboxLabel { get; }

    public override string Title => $"Language and time zone — {MailboxLabel}";

    public override string ConfirmText => "Save";

    public override double Width => 560;

    public IReadOnlyList<ChoiceItem> Languages { get; }

    public IReadOnlyList<ChoiceItem> TimeZones { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private ChoiceItem? _language;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private ChoiceItem? _timeZone;

    /// <summary>Rename Inbox, Sent Items and the rest into the new language. Cannot be undone from here.</summary>
    [ObservableProperty]
    private bool _localizeFolders;

    public override bool CanConfirm => Language is not null && TimeZone is not null;
}
