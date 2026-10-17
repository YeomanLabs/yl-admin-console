using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.Templates;

/// <summary>
/// User templates on disk under <c>templates\users</c> (doc 07), plus the snapshot rules that turn an existing user
/// into one. A template file that will not parse is reported rather than dropped: a template that quietly vanishes is
/// how someone ends up creating an account with the wrong licences.
/// </summary>
public sealed class TemplateService
{
    /// <summary>
    /// Doc 07: the only attributes a snapshot copies. Everything person-specific — names, UPN, mail, mobile, employee
    /// id, hire date — is deliberately absent, because a template is a role, not a person.
    /// </summary>
    public static readonly IReadOnlyList<string> SnapshotAttributes =
    [
        "jobTitle", "department", "companyName", "officeLocation",
        "streetAddress", "city", "state", "postalCode", "country",
        "usageLocation", "preferredLanguage", "employeeType", "businessPhones",
    ];

    private readonly string _directory;
    private readonly GraphClient _graph;
    private readonly ILogger<TemplateService> _logger;

    public TemplateService(string directory, GraphClient graph, ILogger<TemplateService> logger)
    {
        _directory = directory;
        _graph = graph;
        _logger = logger;
    }

    public static string DefaultDirectory => Path.Combine(AppPaths.TemplatesDirectory, "users");

    /// <summary>Reads the AD half of a synced user when snapshotting (M17.1); null leaves templates cloud-only.</summary>
    public OnPrem.IDirectoryService? ActiveDirectory { get; init; }

    /// <summary>Reads the mailbox half when snapshotting (M17.1); null leaves it out.</summary>
    public Exchange.ExchangeService? ExchangeService { get; init; }

    /// <summary>The cached shared mailboxes, scanned for the source user's Full Access (M17.1).</summary>
    public Cache.CacheDb? Cache { get; init; }

    /// <summary>Files that would not load, so the page can say so instead of showing a shorter list.</summary>
    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    public IReadOnlyList<UserTemplate> Load()
    {
        var templates = new List<UserTemplate>();
        var errors = new List<string>();

        if (Directory.Exists(_directory))
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var template = JsonSerializer.Deserialize<UserTemplate>(File.ReadAllText(path), JsonFiles.Options);
                    if (template is null || string.IsNullOrWhiteSpace(template.Name))
                    {
                        errors.Add($"{Path.GetFileName(path)} — no template name");
                        continue;
                    }

                    templates.Add(Normalise(template) with { FileName = Path.GetFileName(path) });
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not read the template {Path}", path);
                    errors.Add($"{Path.GetFileName(path)} — {Explain(ex)}");
                }
            }
        }

        LoadErrors = errors;
        return templates.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public UserTemplate Save(UserTemplate template)
    {
        Directory.CreateDirectory(_directory);
        var fileName = template.FileName ?? $"{JsonFiles.Slug(template.Name)}.json";
        File.WriteAllText(Path.Combine(_directory, fileName), Serialize(template));
        _logger.LogInformation("Saved the template {Name} to {File}", template.Name, fileName);
        return template with { FileName = fileName };
    }

    public void Delete(UserTemplate template)
    {
        if (template.FileName is null) return;
        var path = Path.Combine(_directory, template.FileName);
        if (File.Exists(path)) File.Delete(path);
        _logger.LogInformation("Deleted the template {Name}", template.Name);
    }

    /// <summary>Stamps the last-used date so the picker can order by it (doc 07 §flow step 1).</summary>
    public UserTemplate MarkUsed(UserTemplate template) =>
        template.IsSaved ? Save(template with { LastUsed = DateTimeOffset.UtcNow }) : template;

    /// <summary>The template as it will sit on disk; the editor shows exactly this.</summary>
    public static string Serialize(UserTemplate template) => JsonSerializer.Serialize(template, JsonFiles.Options);

    /// <summary>Parses edited JSON. Returns null and a message rather than throwing at the editor.</summary>
    public static UserTemplate? TryParse(string json, out string? error)
    {
        try
        {
            var template = JsonSerializer.Deserialize<UserTemplate>(json, JsonFiles.Options);
            if (template is null)
            {
                error = "The file is empty.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(template.Name))
            {
                error = "A template needs a name.";
                return null;
            }

            if (template.PasswordPolicy.Length < 8)
            {
                error = "The password length must be at least 8.";
                return null;
            }

            error = null;
            return Normalise(template);
        }
        catch (JsonException ex)
        {
            error = Explain(ex);
            return null;
        }
    }

    /// <summary>
    /// Deserializing <c>object?</c> values gives <see cref="JsonElement"/>, not strings, so every reader that did
    /// "value as string" saw null on a template loaded from disk while an in-memory one worked. Converting once here
    /// means the rest of the app only ever sees plain CLR values.
    /// </summary>
    private static UserTemplate Normalise(UserTemplate template)
    {
        if (template.Attributes.Count == 0) return template;

        var attributes = new Dictionary<string, object?>(template.Attributes.Count, StringComparer.Ordinal);
        foreach (var (key, value) in template.Attributes)
        {
            attributes[key] = value is JsonElement element ? FromJsonValue(element) : value;
        }

        return template with { Attributes = attributes };
    }

    private static object? FromJsonValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.Array => e.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : i.GetRawText()).ToArray(),
        _ => null,
    };

    // ---- snapshot (doc 07 §Snapshot rules) --------------------------------------------------------------------------

    /// <summary>
    /// Builds a template from a live user. Direct licences only, cloud non-dynamic non-role-assignable groups only,
    /// and every exclusion is written into the template so the reason survives the snapshot.
    /// </summary>
    public async Task<UserTemplate> SnapshotAsync(string name, string userId, CancellationToken ct)
    {
        using var doc = await _graph.SendAsync(HttpMethod.Get, $"users/{userId}?$select={GraphSelects.User}", null, ct).ConfigureAwait(false)
            ?? throw new GraphException(0, "EmptyResponse", "Graph returned no body for the user this template is based on.");

        var root = doc.RootElement;
        var user = DirectoryUser.FromJson(root);
        var excluded = new List<string>();

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in SnapshotAttributes)
        {
            if (ReadAttribute(root, property) is { } value) attributes[property] = value;
        }

        var (licenses, licenseNotes) = await SnapshotLicensesAsync(userId, ct).ConfigureAwait(false);
        excluded.AddRange(licenseNotes);

        var (groups, groupNotes) = await SnapshotGroupsAsync(userId, ct).ConfigureAwait(false);
        excluded.AddRange(groupNotes);

        var manager = await ReadManagerAsync(userId, ct).ConfigureAwait(false);
        var onPrem = user.OnPremisesSyncEnabled ? await SnapshotOnPremAsync(user, excluded, ct).ConfigureAwait(false) : null;
        var exchange = await SnapshotExchangeAsync(user, excluded, ct).ConfigureAwait(false);
        if (onPrem is not null && exchange.Addresses.Count > 0) onPrem = onPrem with { AddressRules = exchange.Addresses };

        _logger.LogInformation("Snapshotted a template from {UserId}: {Attributes} attribute(s), {Licenses} licence(s), {Groups} group(s)",
            userId, attributes.Count, licenses.Count, groups.Count);

        return new UserTemplate
        {
            Name = name,
            CreatedFrom = new TemplateSource { UserId = user.Id, Upn = user.UserPrincipalName, At = DateTimeOffset.UtcNow },
            Attributes = attributes,
            ManagerId = manager?.Id,
            ManagerDisplayName = manager?.Label,
            Licenses = licenses,
            Groups = groups,
            ExcludedOnSnapshot = excluded,
            OnPrem = onPrem,
            Exchange = exchange.Settings,
        };
    }

    /// <summary>The AD half of a synced user: its OU, AD groups and role attributes (M17.1).</summary>
    private async Task<TemplateOnPrem?> SnapshotOnPremAsync(DirectoryUser user, List<string> excluded, CancellationToken ct)
    {
        if (ActiveDirectory is null || string.IsNullOrEmpty(user.OnPremisesImmutableId)) return null;
        OnPrem.OnPremUser? ad;
        try
        {
            ad = await ActiveDirectory.GetUserByImmutableIdAsync(user.OnPremisesImmutableId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            excluded.Add($"The AD half — Active Directory could not be reached ({ex.Message})");
            return null;
        }

        if (ad is null)
        {
            excluded.Add("The AD half — no AD account matches this user's source anchor");
            return null;
        }

        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["department"] = ad.Department,
            ["title"] = ad.JobTitle,
            ["company"] = ad.Company,
            ["physicalDeliveryOfficeName"] = ad.Office,
            ["l"] = ad.City,
            ["st"] = ad.State,
            ["co"] = ad.Country,
            ["streetAddress"] = ad.StreetAddress,
            ["postalCode"] = ad.PostalCode,
        };
        foreach (var empty in attributes.Where(a => string.IsNullOrWhiteSpace(a.Value)).Select(a => a.Key).ToList()) attributes.Remove(empty);

        return new TemplateOnPrem
        {
            OuDn = OnPrem.DistinguishedNames.Parent(ad.DistinguishedName) ?? ad.DistinguishedName,
            Groups = [.. ad.MemberOfDns.Select(dn => new TemplateAdGroup(dn, OnPrem.DistinguishedNames.FriendlyPath(dn)))],
            Attributes = attributes,
            UpnSuffix = ad.UserPrincipalName is { } upn && upn.Contains('@') ? upn[(upn.IndexOf('@') + 1)..] : null,
        };
    }

    /// <summary>The mailbox half: regional settings, shared mailboxes the user works in, and addresses as rules (M17.1).</summary>
    private async Task<(TemplateExchange? Settings, IReadOnlyList<string> Addresses)> SnapshotExchangeAsync(DirectoryUser user, List<string> excluded, CancellationToken ct)
    {
        if (ExchangeService is not { IsAvailable: true } exchange) return (null, []);
        var mailbox = await exchange.GetMailboxAsync(user.Id, ct).ConfigureAwait(false);
        if (!mailbox.IsSuccess) return (null, []);

        var regional = (await exchange.GetRegionalAsync(user.Id, ct).ConfigureAwait(false)).Value;
        var access = new List<TemplateMailboxAccess>();
        if (user.UserPrincipalName is { } upn)
        {
            foreach (var shared in Cache?.GetMailboxes(Exchange.MailboxKinds.SharedMailbox).Take(100) ?? [])
            {
                var full = await exchange.GetFullAccessAsync(shared.UserId, ct).ConfigureAwait(false);
                if (full.Value?.Any(e => !e.IsDeny && string.Equals(e.Principal, upn, StringComparison.OrdinalIgnoreCase)) == true)
                {
                    access.Add(new TemplateMailboxAccess { MailboxId = shared.UserId, Label = shared.PrimarySmtp, FullAccess = true });
                }
            }

            try
            {
                foreach (var r in await exchange.GetSendAsGrantedToAsync(upn, ct).ConfigureAwait(false))
                {
                    if (r.ObjectId is not { } id || r.RecipientType?.Contains("Mailbox", StringComparison.OrdinalIgnoreCase) != true) continue;
                    var existing = access.FindIndex(a => a.MailboxId == id);
                    if (existing >= 0) access[existing] = access[existing] with { SendAs = true };
                    else access.Add(new TemplateMailboxAccess { MailboxId = id, Label = r.Display, FullAccess = false, SendAs = true });
                }
            }
            catch (Exchange.ExchangeException ex)
            {
                excluded.Add($"Send As on other mailboxes — {Exchange.ExchangeErrorText.Friendly(ex)}");
            }
        }

        // An extra address becomes a rule when its local part is the person's own: jdoe@contoso.co.uk next to
        // jdoe@contoso.com is "{alias}@contoso.co.uk"; sales@contoso.com is somebody else's and is left out.
        var m = mailbox.Value!;
        var local = m.PrimarySmtp is { } p && p.Contains('@') ? p[..p.IndexOf('@')] : m.Alias;
        var rules = m.EmailAddresses
            .Where(a => a.IsSmtp && !a.IsPrimary && a.Address.Contains('@'))
            .Where(a => string.Equals(a.Address[..a.Address.IndexOf('@')], local, StringComparison.OrdinalIgnoreCase))
            .Select(a => "{alias}" + a.Address[a.Address.IndexOf('@')..])
            .Where(r => !r.EndsWith(".mail.onmicrosoft.com", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (new TemplateExchange { Language = regional?.Language, TimeZone = regional?.TimeZone, MailboxAccess = access }, rules);
    }

    private async Task<(IReadOnlyList<TemplateLicense> Licenses, IReadOnlyList<string> Notes)> SnapshotLicensesAsync(string userId, CancellationToken ct)
    {
        using var doc = await _graph.SendAsync(HttpMethod.Get, $"users/{userId}?$select=licenseAssignmentStates", null, ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("licenseAssignmentStates", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return ([], []);
        }

        var states = list.EnumerateArray().Select(LicenseAssignmentState.FromJson).ToList();

        // Doc 07 keeps the part number next to the id. Without it the file is a list of GUIDs, which is exactly the
        // unreadable template the human-editable format exists to avoid.
        var partNumbers = await ReadSkuPartNumbersAsync(ct).ConfigureAwait(false);
        var direct = states.Where(s => !s.IsGroupAssigned)
            .Select(s => new TemplateLicense
            {
                SkuId = s.SkuId,
                SkuPartNumber = partNumbers.GetValueOrDefault(s.SkuId),
                DisabledPlans = s.DisabledPlans,
            })
            .ToList();

        var inherited = states.Count(s => s.IsGroupAssigned);
        var notes = inherited == 0
            ? Array.Empty<string>()
            : [$"{inherited} group-assigned licence{(inherited == 1 ? string.Empty : "s")} — these come from group membership, not the template"];

        return (direct, notes);
    }

    private async Task<(IReadOnlyList<TemplateGroup> Groups, IReadOnlyList<string> Notes)> SnapshotGroupsAsync(string userId, CancellationToken ct)
    {
        var page = await _graph.GetPageAsync($"users/{userId}/memberOf?$select={GraphSelects.MemberOf}&$top=999", ct).ConfigureAwait(false);

        // memberOf is heterogeneous: it returns directoryRole objects alongside groups, and a role parses as a group
        // perfectly happily. Adding a member to one answers 404, which is exactly what the M7 gate hit.
        var groups = page.Items
            .Where(e => e.GetString("@odata.type")?.Contains("group", StringComparison.OrdinalIgnoreCase) != false)
            .Select(DirectoryGroup.FromJson)
            .ToList();

        var keep = new List<TemplateGroup>();
        var notes = new List<string>();
        foreach (var g in groups)
        {
            var reason = g.MembershipChangeBlockedReason();
            if (reason is null)
            {
                keep.Add(new TemplateGroup { Id = g.Id, DisplayName = g.DisplayName });
            }
            else
            {
                notes.Add($"{g.DisplayName ?? g.Id} — {reason}");
            }
        }

        return (keep, notes);
    }

    /// <summary>SKU id to part number, read live so a template written today names the SKUs this tenant actually has.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ReadSkuPartNumbersAsync(CancellationToken ct)
    {
        try
        {
            var page = await _graph.GetPageAsync("subscribedSkus?$select=skuId,skuPartNumber", ct).ConfigureAwait(false);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sku in page.Items)
            {
                if (sku.GetString("skuId") is { } id && sku.GetString("skuPartNumber") is { } part) map[id] = part;
            }

            return map;
        }
        catch (GraphException ex)
        {
            // A template with bare ids still works; it is only less pleasant to read.
            _logger.LogWarning("Could not read subscribed SKUs for the template: {Code}", ex.ErrorCode);
            return new Dictionary<string, string>();
        }
    }

    private async Task<PersonRef?> ReadManagerAsync(string userId, CancellationToken ct)
    {
        try
        {
            using var doc = await _graph.SendAsync(HttpMethod.Get, $"users/{userId}/manager?$select={GraphSelects.PersonRef}", null, ct).ConfigureAwait(false);
            return doc is null ? null : PersonRef.FromJson(doc.RootElement);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return null;
        }
    }

    /// <summary>A JSON value as something the template file can hold: a string, a bool, or a string array.</summary>
    private static object? ReadAttribute(JsonElement user, string property)
    {
        if (!user.TryGetProperty(property, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
            JsonValueKind.Array => v.EnumerateArray().Select(i => i.GetString()).Where(i => i is not null).ToArray() is { Length: > 0 } a ? a : null,
            _ => null,
        };
    }

    private static string Explain(Exception ex) => ex switch
    {
        JsonException when ex.Message.Contains("missing required properties", StringComparison.OrdinalIgnoreCase)
                           && ex.Message.Contains("name", StringComparison.OrdinalIgnoreCase) => "no template name",
        JsonException => "not valid JSON",
        _ => ex.Message,
    };
}
