using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// One pasted line of a leaver list (M15.5). A line that did not resolve to exactly one person waits for the admin:
/// pick one of the candidates or skip it. Nothing is ever picked on their behalf.
/// </summary>
public sealed partial class OffboardListRow : ObservableObject
{
    private readonly Action _changed;

    public OffboardListRow(ListEntry entry, Action changed)
    {
        Entry = entry;
        _changed = changed;
        Candidates = [.. entry.Candidates.Select(u => new PersonChoice(u))];
    }

    public ListEntry Entry { get; }
    public string Input => Entry.Input;
    public IReadOnlyList<PersonChoice> Candidates { get; }
    public bool HasCandidates => Candidates.Count > 0 && Entry.Match is ListMatch.Ambiguous or ListMatch.NotFound;

    public string Status => Entry.Match switch
    {
        ListMatch.Found => $"{Entry.User!.DisplayName} · {Entry.User.UserPrincipalName}",
        ListMatch.Ambiguous => $"{Candidates.Count} people have this name — choose one, or skip the line",
        ListMatch.Duplicate => "The same person as an earlier line — offboarded once",
        _ when Candidates.Count > 0 => "Nobody has exactly this name or address. Close matches below — choose one only if you are sure",
        _ => "Nobody in the directory matches this — check the spelling, or skip the line",
    };

    public bool IsProblem => Entry.Match is ListMatch.Ambiguous or ListMatch.NotFound;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolved))]
    private PersonChoice? _selectedCandidate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolved))]
    [NotifyPropertyChangedFor(nameof(CanChoose))]
    private bool _isSkipped;

    public bool CanChoose => !IsSkipped;

    /// <summary>The person this line offboards, or null when it is skipped, a duplicate, or still waiting.</summary>
    public DirectoryUser? Chosen => IsSkipped || Entry.Match == ListMatch.Duplicate ? null : Entry.User ?? SelectedCandidate?.User;

    /// <summary>Nothing left to decide on this line.</summary>
    public bool IsResolved => IsSkipped || Entry.Match is ListMatch.Found or ListMatch.Duplicate || SelectedCandidate is not null;

    partial void OnSelectedCandidateChanged(PersonChoice? value) => _changed();

    partial void OnIsSkippedChanged(bool value) => _changed();
}
