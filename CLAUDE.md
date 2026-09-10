# CLAUDE.md — Admin Console

You are building a Windows desktop admin console that unifies Entra ID, Intune, (later) on-prem AD, and (later) Exchange Online into one fast, search-first UI. Read `docs/00-README.md` first, then the doc it points you to for the current task.

**The product is white-label (M11).** It ships branded to nobody: namespace and assembly `AdminConsole`, a neutral
slate-and-blue palette, and `%LOCALAPPDATA%\AdminConsole` on disk. Product name, tagline and the two colours come
from `branding.json` per installation, so a deployment can carry a client's identity. Do not hardcode a company
name, a colour or a logo anywhere — if something needs to differ per deployment, it belongs in `Branding`.

## Non-negotiables

1. **Speed is the product.** No UI action may block the UI thread. Search hits the local SQLite cache, never Graph. Target: palette results < 100 ms on a 5,000-user tenant.
2. **Never make the admin guess.** Every disabled control has an inline reason. Every failure has a human-readable message plus the raw Graph error behind an expander. Every destructive action shows a preview and a live log.
3. **No data leaves the tenant.** No telemetry, no license server, no crash reporting in Phase 1. Zero outbound calls except `login.microsoftonline.com` and `graph.microsoft.com`.
4. **No service accounts, no app-only auth.** Delegated permissions only, personal sign-in, WAM broker on Windows, MSAL token cache protected by DPAPI.
5. **Raw Graph over HttpClient. Do not add the Microsoft.Graph SDK.** We need control over batching, delta, throttling, and payload size. See `docs/04-GRAPH-REFERENCE.md`.
6. **Hybrid-aware from day one.** Any attribute on an `onPremisesSyncEnabled` user that is mastered on-prem is read-only in the UI with the reason "Mastered in on-prem AD (Phase 2)". See `docs/05-DATA-MODEL.md` for the list.
7. **Ask before adding a NuGet package** not already listed in the csproj files.

## Stack (fixed)

- .NET 8, C# 12, WPF, `WPF-UI` (lepoco) for Fluent controls, `CommunityToolkit.Mvvm` for MVVM, `Microsoft.Extensions.Hosting` for DI/config/logging
- `Microsoft.Identity.Client` + `.Broker` + `.Extensions.Msal` for auth
- `Microsoft.Data.Sqlite` (bundled e_sqlite3 has FTS5) for the cache
- `System.Text.Json` for all serialization
- Tests: xUnit + FluentAssertions in `tests/AdminConsole.Core.Tests` (Core only; the WPF project is not unit-tested)

## Repository layout

```
src/AdminConsole.Core/    # no WPF references, fully testable
  Auth/  Graph/  Cache/  Models/  Sync/  Search/  Templates/  Offboarding/  Licensing/
src/AdminConsole.App/     # WPF shell, ViewModels, Views
tests/AdminConsole.Core.Tests/
docs/                                 # the spec — the source of truth
```

Core never references App. App never calls Graph directly — always through a Core service.

## Build / run / test

```powershell
dotnet restore
dotnet build src/AdminConsole.Core -c Debug
dotnet test tests/AdminConsole.Core.Tests
dotnet build src/AdminConsole.App -c Debug   # Windows only
dotnet run --project src/AdminConsole.App
```

Before declaring any task done: `dotnet build` the whole solution with **zero warnings** (warnings are errors in CI later), run tests, and if the task touched UI, launch the app and exercise the feature against the dev tenant.

## Working method

- Work from `docs/09-PLAN.md`. Take the next unchecked task, implement it, verify it, check it off with a one-line note, commit. One task per commit. Don't skip ahead.
- If a task is ambiguous, check the relevant spec doc. If the spec is silent, choose the option that is faster for the end user and note the decision in `docs/DECISIONS.md` (create it if absent).
- If a spec instruction is wrong or impossible (e.g. an endpoint doesn't exist), stop, explain, and propose a change — do not silently deviate.
- Prefer small, boring code. No clever abstractions until the third use.
- All async methods take a `CancellationToken`. All Graph calls go through `GraphClient`. All UI updates marshal via `Dispatcher` or `ObservableObject` property setters on the UI thread.

## Conventions

- Namespaces mirror folders. One public type per file.
- `sealed` by default. `record` for DTOs and models. `required` members over constructor soup.
- ViewModels: `partial class XViewModel : ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`. No code in Views' code-behind beyond `InitializeComponent`.
- Logging via `ILogger<T>`. Never log tokens, passwords, or full user objects. Log object IDs, not UPNs, at Information level.
- Errors: `GraphException` carries `StatusCode`, `ErrorCode`, `Message`, `RequestId`, `RetryAfter`. UI maps `ErrorCode` → friendly text via `docs/02-UX-SPEC.md §7`.
- Secrets: none in repo. `appsettings.json` holds only the client ID and authority. `appsettings.local.json` is gitignored for overrides.

## Things that will bite you

- WPF-UI 3.x: the window is `ui:FluentWindow`; call `ApplicationThemeManager.Apply(ApplicationTheme.Dark)` before showing it; `SystemThemeWatcher.Watch(this)` in the window ctor if following system theme.
- MSAL WAM broker needs the redirect URI `ms-appx-web://microsoft.aad.brokerplugin/{clientId}` registered AND a parent window handle (`.WithParentActivityOrWindow(() => hwnd)`).
- Graph `$batch` is max 20 requests, and each inner request can 429 independently. Handle per-response status.
- `users/delta` does not support `$filter` by arbitrary properties and the first full walk on a large tenant takes minutes — it must run in the background with progress in the status bar.
- `signInActivity` requires Entra ID P1 and `AuditLog.Read.All`; degrade gracefully (show "Not available on this tenant's license").
- `managedDevices` has no delta; refresh on an interval and on demand.
- Removing a member from a group that is dynamic or on-prem synced returns 400/403 — pre-check `groupTypes`/`onPremisesSyncEnabled` and skip with a reason.
