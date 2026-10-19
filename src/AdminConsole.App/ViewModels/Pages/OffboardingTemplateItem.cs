using AdminConsole.Core.Offboarding;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One offboarding template in the Templates page's list: its name, what it is for, and how much it does.</summary>
public sealed record OffboardingTemplateItem(OffboardingProfile Profile, int StepCount, bool IsDefault)
{
    public string Name => Profile.Name;

    public string? Description => string.IsNullOrWhiteSpace(Profile.Description) ? null : Profile.Description;

    public string Summary
    {
        get
        {
            var parts = new List<string> { StepCount == 1 ? "1 step" : $"{StepCount} steps" };
            if (Profile.IsBuiltIn) parts.Add("built in");
            if (IsDefault) parts.Add("opens by default");
            return string.Join(" · ", parts);
        }
    }

    public bool Equals(OffboardingTemplateItem? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => Description is { } d ? $"{Name}, {Summary}. {d}" : $"{Name}, {Summary}";
}
