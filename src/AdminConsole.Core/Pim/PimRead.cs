namespace AdminConsole.Core.Pim;

/// <summary>The admin's PIM roles, or why they could not be read.</summary>
public sealed record PimRead(IReadOnlyList<PimRole> Roles, string? Unavailable)
{
    public bool IsAvailable => Unavailable is null;

    public static PimRead Fail(string reason) => new([], reason);
}
