using AdminConsole.Core.Users.BulkEdit;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>One line of a bulk edit: the preview before it runs, the outcome after.</summary>
public sealed record BulkResultRow(string Label, string Detail, bool IsFailure, bool IsDone)
{
    public static BulkResultRow From(BulkPlanRow row) =>
        new(row.Label, row.HasWork ? string.Join(" · ", row.Changes.Where(c => c.Route != BulkRoute.Unchanged).Select(c => c.Text)) : "Already set — nothing to change",
            row.Changes.Any(c => c.Route == BulkRoute.Refused), false);

    public static BulkResultRow From(BulkUserResult result) =>
        new(result.Label, result.Detail, !result.Succeeded, true);

    public string StatusText => IsDone ? (IsFailure ? "Failed" : "Done") : (IsFailure ? "Can't" : string.Empty);

    public string AccessibleName => $"{Label}: {(StatusText.Length == 0 ? string.Empty : StatusText + ". ")}{Detail}";

    public override string ToString() => AccessibleName;

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so a refilled list whose
    // rows equal the ones before reused their detached peers, and a screen reader lost the rows (0.3.3).
    public bool Equals(BulkResultRow? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
