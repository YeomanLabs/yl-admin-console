using System.Text.Json;

namespace AdminConsole.Core.Models;

/// <summary>
/// One registered authentication method. Not cached — fetched live for the Access tab and the offboarding
/// "remove MFA methods" step. <see cref="DeleteCollectionName"/> is the segment for
/// <c>DELETE /users/{id}/authentication/{collection}/{methodId}</c>; null means it cannot be deleted.
/// </summary>
public sealed record AuthMethod
{
    public required string Id { get; init; }
    public required AuthMethodKind Kind { get; init; }
    public required string ODataType { get; init; }

    /// <summary>Human label: phone number, authenticator device name, FIDO2 key model, email address…</summary>
    public required string DisplayName { get; init; }

    public string? Detail { get; init; }
    public DateTimeOffset? CreatedDateTime { get; init; }
    public required string Raw { get; init; }

    public string? DeleteCollectionName => Kind switch
    {
        AuthMethodKind.Phone => "phoneMethods",
        AuthMethodKind.MicrosoftAuthenticator => "microsoftAuthenticatorMethods",
        AuthMethodKind.Fido2 => "fido2Methods",
        AuthMethodKind.WindowsHelloForBusiness => "windowsHelloForBusinessMethods",
        AuthMethodKind.Email => "emailMethods",
        AuthMethodKind.SoftwareOath => "softwareOathMethods",
        AuthMethodKind.TemporaryAccessPass => "temporaryAccessPassMethods",
        _ => null,
    };

    public bool CanDelete => DeleteCollectionName is not null;

    public string KindLabel => Kind switch
    {
        AuthMethodKind.Password => "Password",
        AuthMethodKind.MicrosoftAuthenticator => "Microsoft Authenticator",
        AuthMethodKind.Phone => "Phone",
        AuthMethodKind.Fido2 => "Passkey (FIDO2)",
        AuthMethodKind.WindowsHelloForBusiness => "Windows Hello for Business",
        AuthMethodKind.Email => "Email",
        AuthMethodKind.SoftwareOath => "Authenticator app (TOTP)",
        AuthMethodKind.TemporaryAccessPass => "Temporary Access Pass",
        _ => "Unknown method",
    };

    public static AuthMethod FromJson(JsonElement e)
    {
        var type = e.GetString("@odata.type") ?? string.Empty;
        var kind = KindFromODataType(type);
        var (display, detail) = kind switch
        {
            AuthMethodKind.Phone => (e.GetString("phoneNumber") ?? "Phone", e.GetString("phoneType")),
            AuthMethodKind.MicrosoftAuthenticator => (e.GetString("displayName") ?? "Authenticator", e.GetString("deviceTag")),
            AuthMethodKind.Fido2 => (e.GetString("displayName") ?? "Passkey", e.GetString("model")),
            AuthMethodKind.WindowsHelloForBusiness => (e.GetString("displayName") ?? "Windows Hello", e.GetString("keyStrength")),
            AuthMethodKind.Email => (e.GetString("emailAddress") ?? "Email", null),
            AuthMethodKind.SoftwareOath => ("Software OATH token", e.GetString("secretKey") is null ? null : "secret present"),
            AuthMethodKind.TemporaryAccessPass => ("Temporary Access Pass", e.GetBool("isUsable") == true ? "usable" : "not usable"),
            AuthMethodKind.Password => ("Password", null),
            _ => (type.Length == 0 ? "Unknown" : type.Replace("#microsoft.graph.", string.Empty, StringComparison.Ordinal), null),
        };

        return new AuthMethod
        {
            Id = e.GetString("id") ?? throw new JsonException("authentication method without id"),
            Kind = kind,
            ODataType = type,
            DisplayName = display,
            Detail = detail,
            CreatedDateTime = e.GetDate("createdDateTime"),
            Raw = e.GetRawText(),
        };
    }

    public static AuthMethodKind KindFromODataType(string odataType)
    {
        var t = odataType.StartsWith("#microsoft.graph.", StringComparison.OrdinalIgnoreCase) ? odataType["#microsoft.graph.".Length..] : odataType;
        return t.ToLowerInvariant() switch
        {
            "passwordauthenticationmethod" => AuthMethodKind.Password,
            "microsoftauthenticatorauthenticationmethod" => AuthMethodKind.MicrosoftAuthenticator,
            "phoneauthenticationmethod" => AuthMethodKind.Phone,
            "fido2authenticationmethod" => AuthMethodKind.Fido2,
            "windowshelloforbusinessauthenticationmethod" => AuthMethodKind.WindowsHelloForBusiness,
            "emailauthenticationmethod" => AuthMethodKind.Email,
            "softwareoathauthenticationmethod" => AuthMethodKind.SoftwareOath,
            "temporaryaccesspassauthenticationmethod" => AuthMethodKind.TemporaryAccessPass,
            _ => AuthMethodKind.Unknown,
        };
    }
}
