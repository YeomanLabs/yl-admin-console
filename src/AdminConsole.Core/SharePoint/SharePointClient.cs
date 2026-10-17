using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;

namespace AdminConsole.Core.SharePoint;

/// <summary>
/// SharePoint Online over <see cref="HttpClient"/> (M16, doc 14): the tenant admin REST API on <c>{name}-admin</c>, and —
/// for the one operation REST does not expose, site collection admins — CSOM's documented wire protocol posted to
/// <c>client.svc/ProcessQuery</c> as plain XML. No CSOM or PnP library is loaded (CLAUDE.md non-negotiable 5).
/// </summary>
/// <remarks>
/// The tenant admin endpoints are undocumented and carry no SLA (PnPjs says the same); each one used here was proven
/// against YeomanLabs on 2026-10-17 and is recorded in doc 14 with the exact request.
/// </remarks>
public sealed class SharePointClient
{
    /// <summary>The CSOM type id of <c>Microsoft.Online.SharePoint.TenantAdministration.Tenant</c>.</summary>
    private const string TenantTypeId = "{268004ae-ef6b-4e9b-8425-127220d84719}";

    private readonly HttpClient _http;
    private readonly IAuthService _auth;
    private readonly ILogger<SharePointClient> _logger;

    public SharePointClient(HttpClient http, IAuthService auth, ILogger<SharePointClient> logger)
    {
        _http = http;
        _auth = auth;
        _logger = logger;
    }

    public int MaxAttempts { get; init; } = 4;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>A REST call. <paramref name="body"/> is JSON (odata=nometadata); the response is parsed, or null when empty.</summary>
    public Task<JsonDocument?> SendAsync(HttpMethod method, string url, object? body, string operation, CancellationToken ct) =>
        SendRawAsync(method, url, body is null ? null : JsonSerializer.Serialize(body), "application/json;odata=nometadata", operation, ct);

    /// <summary>
    /// <c>Tenant.SetSiteAdmin(siteUrl, loginName, isSiteAdmin)</c> as CSOM XML. Adds or removes a site collection admin on
    /// any site — a OneDrive included — without the caller having access to the site first.
    /// </summary>
    public async Task SetSiteAdminAsync(SharePointHosts hosts, string siteUrl, string loginName, bool isSiteAdmin, CancellationToken ct)
    {
        var xml =
            "<Request AddExpandoFieldTypeSuffix=\"true\" SchemaVersion=\"15.0.0.0\" LibraryVersion=\"16.0.0.0\" ApplicationName=\"AdminConsole\" " +
            "xmlns=\"http://schemas.microsoft.com/sharepoint/clientquery/2009\"><Actions>" +
            "<Method Name=\"SetSiteAdmin\" Id=\"2\" ObjectPathId=\"1\"><Parameters>" +
            $"<Parameter Type=\"String\">{SecurityElement.Escape(siteUrl)}</Parameter>" +
            $"<Parameter Type=\"String\">{SecurityElement.Escape(loginName)}</Parameter>" +
            $"<Parameter Type=\"Boolean\">{(isSiteAdmin ? "true" : "false")}</Parameter>" +
            "</Parameters></Method></Actions>" +
            $"<ObjectPaths><Constructor Id=\"1\" TypeId=\"{TenantTypeId}\" /></ObjectPaths></Request>";

        using var doc = await SendRawAsync(HttpMethod.Post, $"{hosts.Admin}/_vti_bin/client.svc/ProcessQuery", xml, "text/xml", "Set site admin", ct).ConfigureAwait(false);

        // CSOM reports failure inside a 200: the first element's ErrorInfo.
        if (doc?.RootElement is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0
            && array[0].TryGetProperty("ErrorInfo", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            var message = info.TryGetProperty("ErrorMessage", out var m) ? m.GetString() ?? "SharePoint refused." : "SharePoint refused.";
            var code = info.TryGetProperty("ErrorCode", out var c) ? c.ToString() : "CSOM";
            var type = info.TryGetProperty("ErrorTypeName", out var t) ? t.GetString() : null;
            throw new SharePointException(200, $"{code}, {type}", message.Replace("\n", " ", StringComparison.Ordinal).Trim(), "Set site admin");
        }
    }

    /// <summary>The claims login name SharePoint wants for a member: <c>i:0#.f|membership|{upn}</c>.</summary>
    public static string LoginNameOf(string upn) => $"i:0#.f|membership|{upn.ToLowerInvariant()}";

    private async Task<JsonDocument?> SendRawAsync(HttpMethod method, string url, string? body, string contentType, string operation, CancellationToken ct)
    {
        var token = await _auth.GetTokenAsync(Scopes.SharePoint, ct).ConfigureAwait(false);
        var refreshed = false;

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json;odata=nometadata");
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _logger.LogWarning(ex, "{Operation}: network error; retrying", operation);
                await Delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new SharePointException(0, SharePointException.NetworkErrorCode, "Can't reach SharePoint Online.", operation, inner: ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);

                if (status == (int)HttpStatusCode.Unauthorized && !refreshed)
                {
                    refreshed = true;
                    token = await _auth.GetTokenAsync(Scopes.SharePoint, ct, forceRefresh: true).ConfigureAwait(false);
                    continue;
                }

                // SharePoint throttles with 429 and 503, and says how long to wait.
                if (status is 429 or 503 && attempt < MaxAttempts)
                {
                    var delay = SharePointException.RetryDelay(response.Headers) ?? Backoff(attempt);
                    _logger.LogWarning("{Operation}: HTTP {Status}; retrying in {Delay}s", operation, status, delay.TotalSeconds);
                    await Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                var ex = SharePointException.FromResponse(status, text, response.Headers, operation);
                _logger.LogWarning("{Operation}: HTTP {Status} {Code}", operation, status, ex.ErrorCode);
                throw ex;
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
}
