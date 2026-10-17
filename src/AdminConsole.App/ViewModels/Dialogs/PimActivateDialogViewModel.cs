using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Pim;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// M18.2 — activate one eligible role: how long for, and whatever the role's policy demands (justification, ticket).
/// What the policy will also ask for that the console cannot supply — approval, a fresh MFA — is said up front.
/// </summary>
public sealed partial class PimActivateDialogViewModel : DialogViewModelBase
{
    public PimActivateDialogViewModel(PimRole role, PimPolicy policy)
    {
        Role = role;
        Policy = policy;
        Durations = [.. policy.Durations.Select(d => new ChoiceItem(d.TotalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture), Describe(d)))];
        _duration = Durations[^1];

        var notes = new List<string>();
        if (policy.RequiresApproval) notes.Add("This role needs approval: the request waits until an approver accepts it.");
        if (policy.RequiresMfa) notes.Add("This role needs multi-factor sign-in at activation. If your sign-in did not use MFA, the activation is refused and says so.");
        if (policy.IsAssumed) notes.Add("The role's policy could not be read, so these are Entra's defaults. The activation still follows the real policy.");
        Notes = notes;
    }

    public PimRole Role { get; }

    public PimPolicy Policy { get; }

    public override string Title => $"Activate {Role.DisplayName}";

    public override string ConfirmText => Policy.RequiresApproval ? "Request activation" : "Activate";

    public IReadOnlyList<ChoiceItem> Durations { get; }

    public IReadOnlyList<string> Notes { get; }

    public bool HasNotes => Notes.Count > 0;

    public string MaximumText => $"Up to {Describe(Policy.MaximumDuration)} for this role";

    [ObservableProperty]
    private ChoiceItem _duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _justification = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _ticketNumber = string.Empty;

    [ObservableProperty]
    private string _ticketSystem = string.Empty;

    public bool ShowTicket => Policy.RequiresTicket;

    public string JustificationLabel => Policy.RequiresJustification ? "Justification (required)" : "Justification";

    public override bool CanConfirm =>
        (!Policy.RequiresJustification || Justification.Trim().Length > 0) && (!Policy.RequiresTicket || TicketNumber.Trim().Length > 0);

    public TimeSpan DurationValue => TimeSpan.FromMinutes(double.Parse(Duration.Value, System.Globalization.CultureInfo.InvariantCulture));

    private static string Describe(TimeSpan d) => d.TotalHours switch
    {
        < 1 => $"{d.TotalMinutes:0} minutes",
        1 => "1 hour",
        var h when h % 24 == 0 => h == 24 ? "1 day" : $"{h / 24:0} days",
        var h => $"{h:0.#} hours",
    };
}
