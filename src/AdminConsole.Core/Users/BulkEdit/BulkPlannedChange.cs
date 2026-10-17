namespace AdminConsole.Core.Users.BulkEdit;

/// <summary>One property of one user as the preview shows it: what it is, what it becomes, and where it is written.</summary>
public sealed record BulkPlannedChange(string Property, string Label, string? From, string? To, BulkRoute Route, string? Reason = null)
{
    public string Text => Route switch
    {
        BulkRoute.Unchanged => $"{Label}: already {Show(To)}",
        BulkRoute.Refused => $"{Label}: {Reason}",
        // The "from" of an AD write is the cloud copy, which can trail AD by a sync cycle, so it is named as such.
        BulkRoute.ActiveDirectory when string.Equals(From ?? string.Empty, To ?? string.Empty, StringComparison.Ordinal) =>
            $"{Label}: {Show(To)} written in AD — the cloud copy shows it already, but AD can be ahead of it",
        BulkRoute.ActiveDirectory => $"{Label}: {Show(To)} written in AD (the cloud copy shows {Show(From)})",
        _ => $"{Label}: {Show(From)} → {Show(To)}",
    };

    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "empty" : $"\"{value}\"";
}
