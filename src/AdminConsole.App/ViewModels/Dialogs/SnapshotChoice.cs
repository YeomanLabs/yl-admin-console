using AdminConsole.Core.Offboarding.Snapshot;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>One snapshot in the restore dialog's picker (M15.4), named by when and by whom it was taken.</summary>
public sealed record SnapshotChoice(string Path, OffboardingSnapshot Snapshot)
{
    public string Label => $"{Snapshot.TakenAt.LocalDateTime:d MMM yyyy, HH:mm}" + (Snapshot.TakenBy is { } by ? $" by {by}" : string.Empty);

    public override string ToString() => Label;
}
