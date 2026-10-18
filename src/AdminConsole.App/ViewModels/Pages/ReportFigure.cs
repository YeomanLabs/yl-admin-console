namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One headline number on a report: "Missing MFA · 4".</summary>
public sealed record ReportFigure(string Label, string Value, bool IsWarning = false)
{
    public override string ToString() => $"{Label}: {Value}";

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so after a rebuild a row equal
    // to the one before took over that row's detached peer and dropped out of what a screen reader can reach (0.3.3).
    public bool Equals(ReportFigure? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
