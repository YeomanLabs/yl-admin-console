# Decisions log

Append-only. Format: `YYYY-MM-DD · <area> · <decision> · <why> · <alternatives rejected>`

- 2026-09-08 · naming · Working name "YeomanLabs Admin Console", namespace `YeomanLabs.AdminConsole` · placeholder until product name chosen · —
- 2026-09-08 · Graph access · Raw HttpClient, no Microsoft.Graph SDK · control over batching/delta/throttling, smaller payloads, matches existing Invoke-MgGraphRequest habits · Graph SDK v5 (heavy, generated types fight `$select`)
- 2026-09-08 · auth · Own multi-tenant public-client app registration with incremental consent · product must be consentable by customers and pass security review; granular scopes over Directory.* · reusing Microsoft Graph CLI first-party app ID (fine for personal use, not for a product)
- 2026-09-08 · UI framework · WPF + WPF-UI 3.x · existing WPF experience, mature tooling; WinUI 3 rejected for tooling friction and unpackaged-app pain
