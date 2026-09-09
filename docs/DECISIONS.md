# Decisions log

Append-only. Format: `YYYY-MM-DD · <area> · <decision> · <why> · <alternatives rejected>`

- 2026-09-08 · naming · Working name "YeomanLabs Admin Console", namespace `YeomanLabs.AdminConsole` · placeholder until product name chosen · —
- 2026-09-08 · Graph access · Raw HttpClient, no Microsoft.Graph SDK · control over batching/delta/throttling, smaller payloads, matches existing Invoke-MgGraphRequest habits · Graph SDK v5 (heavy, generated types fight `$select`)
- 2026-09-08 · auth · Own multi-tenant public-client app registration with incremental consent · product must be consentable by customers and pass security review; granular scopes over Directory.* · reusing Microsoft Graph CLI first-party app ID (fine for personal use, not for a product)
- 2026-09-08 · UI framework · WPF + WPF-UI 3.x · existing WPF experience, mature tooling; WinUI 3 rejected for tooling friction and unpackaged-app pain
- 2026-09-09 · test deps · FluentAssertions pinned to 7.0.0 · 8.x moved to a paid license for non-OSS use; 7.x is the last Apache-2.0 line · FluentAssertions 8.x, Shouldly
- 2026-09-09 · app registration · Created "YeomanLabs Admin Console" in the YeomanLabs Consulting dev tenant (appId 61ef07ab-e035-49e2-b05e-cfd25fc1c1fe, multi-tenant public client, broker + nativeclient + localhost redirects, 22 delegated scopes, admin consent granted) · doc 08 steps 1–5 · —
- 2026-09-09 · scopes · Dropped `Device.ReadWrite.All` from the write tier; offboarding step 9 (delete Entra device objects) deferred to Phase 2 · the scope is application-only on Graph; the only delegated route is `Directory.AccessAsUser.All`, which doc 08 bans as it grants the app the admin's full directory rights · adding `Directory.AccessAsUser.All`
