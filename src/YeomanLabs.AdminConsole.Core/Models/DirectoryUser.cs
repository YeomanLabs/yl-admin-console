using System.Text.Json;

namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>
/// Cache projection of an Entra user (docs/05-DATA-MODEL.md <c>users</c>). <see cref="Raw"/> holds the selected Graph JSON
/// for detail views; nothing here re-fetches from Graph just to render cached data.
/// </summary>
public sealed record DirectoryUser
{
    public const string HybridReadOnlyReason = "Mastered in on-prem AD (Phase 2)";
    public const string SyncedDisableWarning = "Synced account — also disable in on-prem AD or this reverts within 30 minutes.";

    /// <summary>Properties Graph refuses to PATCH on an on-prem-synced user (doc 05 §Hybrid read-only rule).</summary>
    public static readonly IReadOnlySet<string> OnPremMasteredProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "displayName", "givenName", "surname", "userPrincipalName", "mail", "mailNickname", "proxyAddresses",
        "jobTitle", "department", "companyName", "officeLocation", "employeeId", "employeeType",
        "businessPhones", "mobilePhone", "streetAddress", "city", "state", "postalCode", "country", "manager",
    };

    public required string Id { get; init; }
    public required string UserPrincipalName { get; init; }
    public string? DisplayName { get; init; }
    public string? Mail { get; init; }
    public string? GivenName { get; init; }
    public string? Surname { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public string? EmployeeId { get; init; }

    /// <summary>Digits only from businessPhones + mobilePhone, for phone search.</summary>
    public string? PhonesDigits { get; init; }

    public required bool AccountEnabled { get; init; }
    public string? UserType { get; init; }
    public required bool OnPremisesSyncEnabled { get; init; }
    public required bool Licensed { get; init; }
    public DateTimeOffset? CreatedDateTime { get; init; }
    public required string Raw { get; init; }

    // ---- badges (UX §3 row layout: Disabled, Synced, Guest; plus Unlicensed for the sidebar) --------------------

    public bool IsDisabled => !AccountEnabled;
    public bool IsGuest => string.Equals(UserType, "Guest", StringComparison.OrdinalIgnoreCase);
    public bool IsSynced => OnPremisesSyncEnabled;
    public bool IsUnlicensed => !Licensed;

    /// <summary>Palette badges in display order.</summary>
    public IReadOnlyList<string> Badges
    {
        get
        {
            var list = new List<string>(3);
            if (IsDisabled) list.Add("Disabled");
            if (IsSynced) list.Add("Synced");
            if (IsGuest) list.Add("Guest");
            return list;
        }
    }

    // ---- hybrid rule ---------------------------------------------------------------------------------------------

    /// <summary>True when the UI must not offer an edit for <paramref name="propertyName"/> on this user.</summary>
    public bool IsReadOnly(string propertyName) => OnPremisesSyncEnabled && OnPremMasteredProperties.Contains(propertyName);

    /// <summary>The inline reason to show next to a read-only field, or null when editable.</summary>
    public string? ReadOnlyReason(string propertyName) => IsReadOnly(propertyName) ? HybridReadOnlyReason : null;

    /// <summary>
    /// <c>accountEnabled</c> is accepted by Graph on a synced user but reverts on the next Entra Connect cycle;
    /// the write is allowed with this caption (doc 05 note **).
    /// </summary>
    public string? WriteWarning(string propertyName) =>
        OnPremisesSyncEnabled && string.Equals(propertyName, "accountEnabled", StringComparison.OrdinalIgnoreCase) ? SyncedDisableWarning : null;

    // ---- parsing -------------------------------------------------------------------------------------------------

    public static DirectoryUser FromJson(JsonElement e)
    {
        var phones = string.Concat(e.GetStrings("businessPhones").Select(JsonProps.DigitsOnly)) + JsonProps.DigitsOnly(e.GetString("mobilePhone"));
        return new DirectoryUser
        {
            Id = e.GetString("id") ?? throw new JsonException("user without id"),
            UserPrincipalName = e.GetString("userPrincipalName") ?? string.Empty,
            DisplayName = e.GetString("displayName"),
            Mail = e.GetString("mail"),
            GivenName = e.GetString("givenName"),
            Surname = e.GetString("surname"),
            JobTitle = e.GetString("jobTitle"),
            Department = e.GetString("department"),
            EmployeeId = e.GetString("employeeId"),
            PhonesDigits = phones.Length == 0 ? null : phones,
            AccountEnabled = e.GetBool("accountEnabled") ?? false,
            UserType = e.GetString("userType"),
            OnPremisesSyncEnabled = e.GetBool("onPremisesSyncEnabled") ?? false,
            Licensed = e.ArrayLength("assignedLicenses") > 0,
            CreatedDateTime = e.GetDate("createdDateTime"),
            Raw = e.GetRawText(),
        };
    }

    /// <summary>Re-parses <see cref="Raw"/> for detail rendering. Caller disposes.</summary>
    public JsonDocument ParseRaw() => JsonDocument.Parse(Raw);
}
