using System.Text.Json;

namespace AdminConsole.Core.Templates;

/// <summary>Which of the two template files a piece of JSON is.</summary>
public enum TemplateFileKind
{
    Unknown,
    User,
    Offboarding,
}

/// <summary>
/// Moving templates between PCs and tenants (Templates → Import / Export). A template file is the same JSON the
/// Templates folder holds, so an exported file can also be dropped into that folder by hand. User templates name one
/// tenant's objects — groups, licences, a manager, an OU — so an import into another tenant says what it found missing
/// rather than leaving the create form to fail on it later.
/// </summary>
public static class TemplateTransfer
{
    /// <summary>
    /// User template files always carry <c>schemaVersion</c>; offboarding template files always carry <c>steps</c>.
    /// Telling them apart stops a file imported on the wrong tab becoming an empty template of the other kind.
    /// </summary>
    public static TemplateFileKind KindOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return TemplateFileKind.Unknown;
            var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Contains("steps")) return TemplateFileKind.Offboarding;
            if (names.Contains("schemaVersion") || names.Contains("upnRule")) return TemplateFileKind.User;
            return TemplateFileKind.Unknown;
        }
        catch (JsonException)
        {
            return TemplateFileKind.Unknown;
        }
    }

    /// <summary>The file name an export suggests: the template's own slug.</summary>
    public static string ExportFileName(string name, TemplateFileKind kind) =>
        kind == TemplateFileKind.Offboarding ? $"{JsonFiles.Slug(name)}.offboarding.json" : $"{JsonFiles.Slug(name)}.json";

    /// <summary>A name not already taken: "Sales rep", else "Sales rep (imported)", then "(imported 2)" and on.</summary>
    public static string UniqueName(string name, IEnumerable<string> taken)
    {
        var used = taken.Select(t => t.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trimmed = name.Trim();
        if (!used.Contains(trimmed)) return trimmed;
        var candidate = $"{trimmed} (imported)";
        for (var n = 2; used.Contains(candidate); n++) candidate = $"{trimmed} (imported {n})";
        return candidate;
    }

    /// <summary>
    /// What a user template names that this tenant does not have, in words. Groups and the manager are looked up by
    /// object id, which differs between tenants; licences by SKU id, which is the same product everywhere but may not be
    /// subscribed here.
    /// </summary>
    public static IReadOnlyList<string> MissingHere(UserTemplate template, Func<string, bool> groupExists, Func<string, bool> skuSubscribed, Func<string, bool> userExists)
    {
        var missing = new List<string>();

        var groups = template.Groups.Where(g => !groupExists(g.Id)).ToList();
        if (groups.Count > 0)
        {
            missing.Add($"{Count(groups.Count, "group")} not in this tenant ({string.Join(", ", groups.Select(g => g.DisplayName ?? g.Id))}). Adding someone to them would fail; swap in this tenant's groups in the template's JSON.");
        }

        var licences = template.Licenses.Where(l => !skuSubscribed(l.SkuId)).ToList();
        if (licences.Count > 0)
        {
            missing.Add($"{Count(licences.Count, "licence")} not subscribed here ({string.Join(", ", licences.Select(l => l.SkuPartNumber ?? l.SkuId))}); assigning them would fail.");
        }

        if (template.ManagerId is { Length: > 0 } manager && !userExists(manager))
        {
            missing.Add($"The manager ({template.ManagerDisplayName ?? manager}) is not in this tenant; choose one in the form.");
        }

        if (template.OnPrem is not null)
        {
            missing.Add("It has an AD part from wherever it was made: check its OU and groups exist in this tenant's domain before creating in AD.");
        }

        return missing;
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
