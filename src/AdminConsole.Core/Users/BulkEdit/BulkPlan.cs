namespace AdminConsole.Core.Users.BulkEdit;

/// <summary>What a bulk edit will do, person by person, before anything is sent.</summary>
public sealed record BulkPlan(IReadOnlyList<BulkPlanRow> Rows, IReadOnlyList<BulkChange> Changes, IReadOnlyList<string> Problems)
{
    public int WithWork => Rows.Count(r => r.HasWork);

    public int Writes(BulkRoute route) => Rows.Sum(r => r.Changes.Count(c => c.Route == route));

    /// <summary>One line for the dialog: "12 people · 10 in Entra ID, 2 in AD · 3 already set".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Rows.Count} {(Rows.Count == 1 ? "person" : "people")}" };
            var entra = Rows.Count(r => r.ToEntra.Count > 0);
            var ad = Rows.Count(r => r.ToDirectory.Count > 0);
            if (entra > 0 || ad > 0) parts.Add(string.Join(", ", new[] { entra > 0 ? $"{entra} in Entra ID" : null, ad > 0 ? $"{ad} in AD" : null }.Where(s => s is not null)));
            var idle = Rows.Count(r => !r.HasWork);
            if (idle > 0) parts.Add($"{idle} already set");
            return string.Join(" · ", parts);
        }
    }
}
