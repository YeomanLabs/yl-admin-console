using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// M18.1 — the choices for a Temporary Access Pass: how long it lasts, whether it works once, and when it starts. The
/// pass is shown afterwards in the show-once dialog; this one only asks.
/// </summary>
public sealed partial class TapDialogViewModel : DialogViewModelBase
{
    /// <summary>The tenant's policy sets the real range (by default one to eight hours); Graph refuses outside it and says the range.</summary>
    public static IReadOnlyList<ChoiceItem> Lifetimes { get; } =
    [
        new("60", "1 hour"),
        new("120", "2 hours"),
        new("240", "4 hours"),
        new("480", "8 hours"),
        new("1440", "1 day"),
        new("4320", "3 days"),
    ];

    public TapDialogViewModel(string userLabel)
    {
        Message = $"{userLabel} can sign in with the pass instead of a password to register MFA or a passkey. It is shown once, after it is issued.";
        _lifetime = Lifetimes[0];
    }

    public override string Title => "Issue a Temporary Access Pass";

    public override string ConfirmText => "Issue pass";

    [ObservableProperty]
    private ChoiceItem _lifetime;

    [ObservableProperty]
    private bool _usableOnce = true;

    /// <summary>Today means now. A later day starts at 08:00 local — the day-one case, issued the afternoon before.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartCaption))]
    private DateTime? _startDate = DateTime.Today;

    public int LifetimeMinutes => int.Parse(Lifetime.Value, CultureInfo.InvariantCulture);

    public DateTimeOffset? Start => StartDate is { } d && d.Date > DateTime.Today
        ? new DateTimeOffset(d.Date.AddHours(8), TimeZoneInfo.Local.GetUtcOffset(d.Date.AddHours(8)))
        : null;

    public string StartCaption => Start is { } s ? $"Starts {s:dddd d MMMM} at 08:00" : "Starts as soon as it is issued";
}
