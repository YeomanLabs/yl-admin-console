using System.Text;
using System.Text.Json;

namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>
/// Reads claims out of an access token we obtained ourselves. No signature validation — we are not the
/// audience and never trust these values for authorization; they only drive UI hints (the grey rule).
/// </summary>
public static class AccessTokenClaims
{
    /// <summary>Returns the space-separated <c>scp</c> claim as a list, or empty if the token is not a parsable JWT.</summary>
    public static IReadOnlyList<string> GetScopes(string accessToken)
    {
        using var payload = TryReadPayload(accessToken);
        if (payload is null) return [];
        if (!payload.RootElement.TryGetProperty("scp", out var scp) || scp.ValueKind != JsonValueKind.String) return [];
        return scp.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Returns the <c>tid</c> claim, or null.</summary>
    public static string? GetTenantId(string accessToken)
    {
        using var payload = TryReadPayload(accessToken);
        if (payload is null) return null;
        return payload.RootElement.TryGetProperty("tid", out var tid) && tid.ValueKind == JsonValueKind.String
            ? tid.GetString()
            : null;
    }

    /// <summary>Test helper: builds an unsigned JWT-shaped token carrying <paramref name="payload"/>.</summary>
    public static string CreateUnsignedToken(object payload)
    {
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        var body = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.";
    }

    private static JsonDocument? TryReadPayload(string accessToken)
    {
        if (string.IsNullOrEmpty(accessToken)) return null;
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            return JsonDocument.Parse(Base64UrlDecode(parts[1]));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        return Convert.FromBase64String(s);
    }
}
