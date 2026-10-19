# 03 — Architecture

## Projects
- **Core** (`net8.0`, no Windows dependency except MSAL broker package): auth, Graph client, cache, sync, search, templates, offboarding, licensing. Everything here is unit-testable with a fake `HttpMessageHandler` and an in-memory SQLite (`Data Source=:memory:`).
- **App** (`net8.0-windows`): WPF shell, ViewModels, Views, DI wiring. Thin.
- **Core.Tests**: xUnit.

## Dependency injection
`App.xaml.cs` builds a `Host` with `Host.CreateDefaultBuilder()`:
- `appsettings.json` + `appsettings.local.json` (optional) bound to `AuthOptions`, `SyncOptions`.
- App-wide singletons: the shell and window, toasts, dialogs, `ActionLog` (rows carry their tenant), the file stores (settings, branding, `tenant.json`, snapshots, pending creates, reports, offboarding templates), the shared `HttpClient`, and `TenantSessionFactory`.
- **Per tenant (M20):** everything else — `TenantContext`, `AuthService`, `GraphClient`, the Exchange and SharePoint clients, `CacheDb`, `DeltaSyncService`, `SearchService`, every Core service and every page ViewModel — lives in a `TenantSession`, a container of its own built by `App.ConfigureTenantServices` for one tenant profile. The app-wide singletons are passed into it, so there is still one of each. Each tab holds the session it was opened in; the shell's title bar, sync pill, status bar and palette follow the selected tab. Each session runs its own sync loop (there is no hosted service any more).
- Transient: every ViewModel (resolved from the tab's session). Views are created by XAML with `DataContext` set by a `ViewModelLocator`-free pattern: the shell VM exposes child VMs; navigation is `ContentControl` + `DataTemplate` per VM type.
- `ILogger` → file sink in `%LOCALAPPDATA%\YeomanLabs\AdminConsole\logs\` rolling daily, plus Debug output. Keep it to `Microsoft.Extensions.Logging` + a 40-line file logger provider; do not add Serilog in Phase 1.

## Paths
`%LOCALAPPDATA%\YeomanLabs\AdminConsole\`
- `cache\{tenantId}.db` — SQLite cache
- `msal.cache` — MSAL token cache (DPAPI via MsalCacheHelper)
- `templates\*.json` — user templates
- `logs\`
- `actions.db` — local action log (what this admin did, with outcomes)

## Auth flow
1. `AuthService.EnsureSignedInAsync(scopes)`: try `AcquireTokenSilent` for the cached account; on `MsalUiRequiredException` → `AcquireTokenInteractive` with WAM broker, parent hwnd from the main window.
2. Two scope sets, from `docs/08-APP-REGISTRATION.md`: `ReadScopes` requested at first run; `WriteScopes` requested lazily (incremental consent) the first time a write is attempted. `AuthService.HasScopeAsync(scope)` inspects the cached token's `scp` claim so the UI can apply the grey rule *before* the user clicks.
3. `TenantContext` holds `TenantId`, `TenantDisplayName`, `SignedInUserId`, `SignedInUpn`, granted scopes, and `HasP1` (from `organization.assignedPlans` containing `AAD_PREMIUM`). Everything downstream reads it.
4. Token acquisition is centralized: `GraphClient` asks `AuthService` for a token per request (MSAL caches; cost is negligible).

## GraphClient
- Single `HttpClient` with `BaseAddress = https://graph.microsoft.com/v1.0/`, `SocketsHttpHandler` with `PooledConnectionLifetime = 5 min`. Set `User-Agent: YeomanLabs-AdminConsole/0.1`. `ConsistencyLevel: eventual` header added per call when `$count`/`$search` is used.
- `SendAsync(HttpMethod, string relativeUrl, object? body, CancellationToken)` → `JsonDocument`. Handles: 401 (one silent token refresh + retry), 429/503/504 (honor `Retry-After`, else exponential backoff 1-2-4-8 s with jitter, max 5 attempts), 5xx (same), 4xx (throw `GraphException` with parsed `error.code`, `error.message`, `request-id`, `client-request-id`).
- `GetAllPagesAsync(url, ct)` follows `@odata.nextLink`, yields `IAsyncEnumerable<JsonElement>`.
- `BatchAsync(IReadOnlyList<BatchRequest>, ct)` chunks to 20, POSTs `/$batch`, maps responses by `id`, applies the same retry logic *per inner response* (collect the 429s, wait max Retry-After, resend only those).
- Concurrency: a `SemaphoreSlim(4)` per client. Graph throttles per app-per-tenant; 4 concurrent + batching is plenty.
- Beta endpoint access via `SendBetaAsync` — used only where documented in `04-GRAPH-REFERENCE.md`. Every beta call has a comment explaining why v1.0 can't do it.

## Cache & sync
- `CacheDb` opens `cache\{tenantId}.db`, WAL mode, `synchronous=NORMAL`, applies migrations from embedded SQL (`Cache/Migrations/0001_init.sql`…). Schema in `05-DATA-MODEL.md`.
- Objects are stored as a **projection** (columns used for search/list) **plus the raw JSON** of the selected properties (`raw` TEXT). Detail views deserialize `raw`; nothing re-fetches from Graph just to render cached data.
- `DeltaSyncService` runs as a hosted background loop:
  1. On tenant open: if no `delta_link` for `users`, do the **initial walk** (`/users/delta?$select=…`), page by page, writing in transactions of 500 rows, publishing `SyncProgress` events (entity, processed, done?). Then groups (`/groups/delta?$select=…,members`), then `/devices/delta`, then Intune `managedDevices` (full page walk, no delta) and `subscribedSkus`.
  2. Store each `@odata.deltaLink` in `sync_state`. Subsequent ticks call the delta link, apply upserts and `@removed` deletes, and for groups apply `members@delta` adds/removes to `group_members`.
  3. Interval from `SyncOptions.DeltaIntervalSeconds` (default 120). Intune devices refresh on `DeviceRefreshIntervalSeconds` (default 600). Manual trigger via `SyncNowAsync()`.
  4. If Graph returns `syncStateNotFound` / 410 for a delta link, wipe that entity table and redo the initial walk.
- Sync never touches the UI thread; it publishes to `ISyncStatus` (an `IObservable`-like event source) and the shell VM marshals.
- **Write-through:** after a successful write (e.g. PATCH user), `GraphClient` returns the response; the calling service updates the cache row immediately so the UI reflects it before the next delta.

## Search
- FTS5 virtual tables `users_fts`, `groups_fts`, `devices_fts` (external-content tables over the base tables, kept in sync by triggers).
- `SearchService.SearchAsync(query, scope, limit, ct)`: normalize (trim, lowercase, digits-only variant for phone), build an FTS `MATCH` with prefix tokens (`term*`), take top 40 by `bm25`, then re-rank in memory using the rules in `02-UX-SPEC.md §3`, apply recent-object boost from `recent_objects`, return grouped results.
- Must be sub-100 ms; measure in a test with 5,000 synthetic users.

## Threading rules
- Core is thread-agnostic and never touches `Dispatcher`.
- ViewModels receive results via `await` on the UI thread (default WPF `SynchronizationContext`). Long operations run on the thread pool via the Core service; VMs only await.
- `ObservableCollection` mutations happen on the UI thread only. Bulk replace via a `RangeObservableCollection` helper to avoid per-item notifications.

## Action log
`actions.db`: every write the app performs — timestamp, actor, tenant, target object id/type, action, request summary, outcome, Graph request-id, and (if reversible) the inverse payload. Powers the Activity nav item, the Undo toasts, and the offboarding "Copy log". Never stores passwords or tokens.

## Extension seams for later phases
- `IDirectorySource` — Phase 2 adds `OnPremAdSource` alongside `EntraSource`; the User 360 merges by `onPremisesImmutableId` ↔ `ms-DS-ConsistencyGuid`.
- `IOffboardingStep.RequiredCapability` — `Graph`, `Exchange`, `OnPremAd`. Runner skips steps whose capability isn't connected, with the grey reason.
- `TenantContext` is a class, not a static — Phase 3 makes it a per-tab instance. Done in M20: one per `TenantSession`.
