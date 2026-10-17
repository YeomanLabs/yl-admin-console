namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One headline number on a report: "Missing MFA · 4".</summary>
public sealed record ReportFigure(string Label, string Value, bool IsWarning = false)
{
    public override string ToString() => $"{Label}: {Value}";
}
