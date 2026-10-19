using System.Text.RegularExpressions;

namespace AdminConsole.Core.Offboarding;

/// <summary>
/// The placeholders an offboarding template's automatic reply may use. A template is written once and used for many
/// leavers, so a fixed sentence cannot name the person or who to contact instead; these fill that in per run. Matching
/// ignores case, and anything in braces that is not one of these is reported rather than sent to customers as is.
/// </summary>
public static partial class AutoReplyText
{
    public const string Name = "{name}";
    public const string FirstName = "{firstName}";
    public const string Email = "{email}";
    public const string Delegate = "{delegate}";
    public const string DelegateEmail = "{delegateEmail}";
    public const string Company = "{company}";

    /// <summary>Every placeholder with what it becomes, for the editor's help text.</summary>
    public static readonly IReadOnlyList<(string Token, string Meaning)> Tokens =
    [
        (Name, "the leaver's display name"),
        (FirstName, "their first name"),
        (Email, "their mail address"),
        (Delegate, "the delegate's name"),
        (DelegateEmail, "the delegate's mail address"),
        (Company, "the organisation's name"),
    ];

    [GeneratedRegex(@"\{[^{}\s]*\}")]
    private static partial Regex Braces();

    /// <summary>Text in braces that is not a placeholder, as written, in the order it first appears.</summary>
    public static IReadOnlyList<string> UnknownTokens(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : Braces().Matches(text).Select(m => m.Value).Where(t => !IsKnown(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether the text names the delegate, so it cannot be sent until one is chosen.</summary>
    public static bool NeedsDelegate(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text.Contains(Delegate, StringComparison.OrdinalIgnoreCase) || text.Contains(DelegateEmail, StringComparison.OrdinalIgnoreCase));

    /// <summary>The text with every placeholder filled in. Unknown ones are left as written, so the preview shows them.</summary>
    public static string Render(string text, Values values) =>
        Braces().Replace(text, m => m.Value.ToLowerInvariant() switch
        {
            "{name}" => values.Name,
            "{firstname}" => values.FirstName,
            "{email}" => values.Email,
            "{delegate}" => values.Delegate ?? m.Value,
            "{delegateemail}" => values.DelegateEmail ?? values.Delegate ?? m.Value,
            "{company}" => values.Company,
            _ => m.Value,
        });

    /// <summary>The values for one run: the leaver, the delegate and the tenant.</summary>
    public static Values For(OffboardingContext ctx)
    {
        var name = ctx.User.DisplayName ?? ctx.UserLabel;
        var first = string.IsNullOrWhiteSpace(ctx.User.GivenName) ? name.Split(' ', 2)[0] : ctx.User.GivenName!;
        return new Values
        {
            Name = name,
            FirstName = first,
            Email = ctx.User.Mail ?? ctx.User.UserPrincipalName ?? name,
            Delegate = ctx.Delegate?.Label,
            DelegateEmail = ctx.Delegate?.UserPrincipalName,
            Company = string.IsNullOrWhiteSpace(ctx.Tenant.TenantDisplayName) ? "our organisation" : ctx.Tenant.TenantDisplayName,
        };
    }

    private static bool IsKnown(string token) => Tokens.Any(t => string.Equals(t.Token, token, StringComparison.OrdinalIgnoreCase));

    public sealed record Values
    {
        public required string Name { get; init; }
        public required string FirstName { get; init; }
        public required string Email { get; init; }
        public string? Delegate { get; init; }
        public string? DelegateEmail { get; init; }
        public required string Company { get; init; }
    }
}
