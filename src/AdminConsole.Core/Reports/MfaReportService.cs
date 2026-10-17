using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;

namespace AdminConsole.Core.Reports;

/// <summary>
/// M19.1 — who can do MFA, who cannot yet, what each is missing, and who is not expected to. One paged read of
/// <c>reports/authenticationMethods/userRegistrationDetails</c> (the same source as the portal's User registration
/// details), joined with the cache for sign-in state and mailbox kind.
/// </summary>
public sealed class MfaReportService
{
    public const string Kind = "mfa";

    /// <summary>Methods that are a real second factor. Phone methods count, but only as <see cref="MfaStatus.PhoneOnly"/>.</summary>
    private static readonly HashSet<string> Strong = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoftAuthenticatorPush", "microsoftAuthenticatorPasswordless", "softwareOneTimePasscode", "hardwareOneTimePasscode",
        "fido2", "passKeyDeviceBound", "passKeyDeviceBoundAuthenticator", "passKeyDeviceBoundWindowsHello", "passKeySynced",
        "windowsHelloForBusiness", "macOsSecureEnclaveKey",
    };

    private static readonly HashSet<string> Phone = new(StringComparer.OrdinalIgnoreCase) { "mobilePhone", "alternateMobilePhone", "officePhone" };

    private readonly GraphClient _graph;
    private readonly CacheDb _db;
    private readonly ILogger<MfaReportService> _logger;

    public MfaReportService(GraphClient graph, CacheDb db, ILogger<MfaReportService> logger)
    {
        _graph = graph;
        _db = db;
        _logger = logger;
    }

    public async Task<MfaReport> BuildAsync(CancellationToken ct)
    {
        var rows = new List<MfaRow>();
        DateTimeOffset? updated = null;
        var capable = 0;

        await foreach (var item in _graph.GetAllPagesAsync("reports/authenticationMethods/userRegistrationDetails?$top=999", ct).ConfigureAwait(false))
        {
            var id = Text(item, "id");
            if (id is null) continue;
            var methods = item.TryGetProperty("methodsRegistered", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                : [];
            if (Date(item, "lastUpdatedDateTime") is { } at && (updated is null || at > updated)) updated = at;
            if (Bool(item, "isMfaCapable")) capable++;

            var user = _db.GetUser(id);
            var guest = string.Equals(Text(item, "userType"), "guest", StringComparison.OrdinalIgnoreCase);
            var mailbox = _db.GetMailbox(id);
            var upn = Text(item, "userPrincipalName") ?? user?.UserPrincipalName ?? id;
            var (status, detail) = IsSyncAccount(upn, Text(item, "userDisplayName"))
                ? (MfaStatus.Exempt, "Entra Connect's sync account — it signs in as a service, without MFA, by design")
                : Classify(methods, Bool(item, "isMfaRegistered"), guest, user?.AccountEnabled, mailbox?.Kind);

            rows.Add(new MfaRow
            {
                UserId = id,
                DisplayName = Text(item, "userDisplayName") ?? user?.DisplayName ?? id,
                Upn = upn,
                IsAdmin = Bool(item, "isAdmin"),
                IsGuest = guest,
                Enabled = user?.AccountEnabled,
                Status = status,
                Detail = detail,
                Methods = methods,
                PasswordlessCapable = Bool(item, "isPasswordlessCapable"),
            });
        }

        _logger.LogInformation("MFA report: {Count} people, {Missing} missing", rows.Count, rows.Count(r => r.Status == MfaStatus.Missing));
        var listed = rows.Select(r => r.UserId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new MfaReport
        {
            TakenAt = DateTimeOffset.Now,
            BlockedLeftOut = _db.GetBlockedUserIds().Count(id => !listed.Contains(id)),
            SourceUpdatedAt = updated,
            MfaCapable = capable,
            Rows = [.. rows.OrderBy(r => r.Status).ThenByDescending(r => r.IsAdmin).ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
        };
    }

    /// <summary>The status and its one-sentence reason. Exemption is decided first: a blocked account's missing MFA is not a gap.</summary>
    internal static (MfaStatus, string) Classify(IReadOnlyList<string> methods, bool registered, bool guest, bool? enabled, MailboxKind? mailbox)
    {
        if (enabled == false) return (MfaStatus.Exempt, "Sign-in is blocked");
        if (guest) return (MfaStatus.Exempt, "Guest — MFA is done in their home tenant");
        if (mailbox is MailboxKind.Shared or MailboxKind.Room or MailboxKind.Equipment)
        {
            return (MfaStatus.Exempt, $"{mailbox} mailbox — people reach it through their own sign-in");
        }

        if (methods.Any(Strong.Contains)) return (MfaStatus.Ready, "Has " + string.Join(", ", methods.Where(Strong.Contains).Select(MethodName).Distinct()));
        if (methods.Any(Phone.Contains)) return (MfaStatus.PhoneOnly, "Phone only — add the Authenticator app or a passkey");
        return (MfaStatus.Missing, registered ? "Registered, but with nothing that can do MFA" : "No second factor registered");
    }

    /// <summary>
    /// The account Entra Connect creates for itself (<c>Sync_{server}_{id}@….onmicrosoft.com</c>). It holds the
    /// Directory Synchronization Accounts role, so Entra calls it an admin, and it can never register MFA.
    /// </summary>
    internal static bool IsSyncAccount(string upn, string? displayName) =>
        (upn.StartsWith("Sync_", StringComparison.OrdinalIgnoreCase) && upn.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase))
        || string.Equals(displayName, "On-Premises Directory Synchronization Service Account", StringComparison.OrdinalIgnoreCase);

    public static string MethodName(string method) => method switch
    {
        "microsoftAuthenticatorPush" => "Authenticator app",
        "microsoftAuthenticatorPasswordless" => "Authenticator passwordless",
        "softwareOneTimePasscode" => "OTP app",
        "hardwareOneTimePasscode" => "OTP token",
        "fido2" or "passKeyDeviceBound" or "passKeyDeviceBoundAuthenticator" or "passKeySynced" => "Passkey",
        "passKeyDeviceBoundWindowsHello" or "windowsHelloForBusiness" => "Windows Hello",
        "macOsSecureEnclaveKey" => "Platform credential (Mac)",
        "mobilePhone" => "Mobile phone",
        "alternateMobilePhone" => "Alternate phone",
        "officePhone" => "Office phone",
        "email" => "Email (reset only)",
        "securityQuestion" => "Security questions (reset only)",
        "temporaryAccessPass" => "Temporary Access Pass",
        _ => method,
    };

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var d) ? d : null;
}
