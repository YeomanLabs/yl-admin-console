using AdminConsole.Core.Offboarding;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>The steps an offboarding template ticks, grouped by the system they change, in run order within each.</summary>
public sealed record TemplateStepGroup(string Title, IReadOnlyList<TemplateStepRow> Rows)
{
    public static IReadOnlyList<TemplateStepGroup> From(IEnumerable<TemplateStepRow> rows) =>
        rows.GroupBy(r => r.Step.Surface)
            .OrderBy(g => Order(g.Key))
            .Select(g => new TemplateStepGroup(TitleOf(g.Key), g.ToList()))
            .ToList();

    private static string TitleOf(StepSurface surface) => surface switch
    {
        StepSurface.Exchange => "Exchange Online",
        StepSurface.SharePoint => "OneDrive",
        StepSurface.OnPremAd => "On-prem Active Directory",
        _ => "Entra ID and Intune",
    };

    private static int Order(StepSurface surface) => surface switch
    {
        StepSurface.Graph => 0,
        StepSurface.Exchange => 1,
        StepSurface.SharePoint => 2,
        _ => 3,
    };

    public bool Equals(TemplateStepGroup? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => Title;
}
