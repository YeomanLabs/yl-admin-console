# 09 — Phase 1 build plan

Work top to bottom. One task = one commit. Check the box and add a one-line note (`— done: <what/how verified>`). Do not start a task whose predecessors are unchecked. Each milestone ends with a **gate** you must run and pass.

Legend: 🧪 has a unit test requirement · 🖥 requires launching the app against the dev tenant.

## M0 — Repo hygiene
- [x] 0.1 Add `tests/YeomanLabs.AdminConsole.Core.Tests` (xUnit, FluentAssertions) to the solution; add a trivial passing test. — done: xunit 2.9.2 + FluentAssertions 7.0.0 (last Apache-2.0 line) + Test.Sdk 17.11.1; `dotnet test` 1/1 passed, 0 warnings
- [ ] 0.2 Add `Directory.Build.props` with `TreatWarningsAsErrors=false` for now, `Nullable=enable`, `AnalysisLevel=latest`, deterministic builds.
- [ ] 0.3 Add `docs/DECISIONS.md` with the header and the first entry: chosen working name/namespace.
- [ ] 0.4 Add a GitHub Actions workflow `build.yml` on `windows-latest`: restore, build solution, test. (Will fail until App compiles — that's fine; it's the gate for M3.)
- **Gate:** `dotnet build src/YeomanLabs.AdminConsole.Core && dotnet test` green.

## M1 — Core: auth + Graph client
- [ ] 1.1 `Auth/AuthOptions` (ClientId, Authority), `Auth/Scopes` (Read/Write/Sensitive constants from doc 08).
- [ ] 1.2 `Auth/AuthService`: MSAL PCA builder with broker, `MsalCacheHelper` at the path in doc 03, `EnsureSignedInAsync(scopes, ct)`, `SignOutAsync`, `GetTokenAsync(scopes, ct)`, `HasScopeAsync(scope, ct)` (parse `scp` from the cached access token JWT payload without validation — it's our own token), `AccountChanged` event. Parent window handle via injected `Func<IntPtr>`.
- [ ] 1.3 🧪 `Graph/GraphException`, `Graph/GraphClient` per doc 03 (send, paging, retry, per-inner-response batch retry). Tests with a fake handler: 429 with Retry-After then 200; nextLink paging of 3 pages; batch with one inner 429; 4xx surfaces code/message/request-id.
- [ ] 1.4 `Graph/GraphSelects` constants for user/group/device/managedDevice from doc 04.
- [ ] 1.5 `Auth/TenantContext` populated by `TenantContextLoader.LoadAsync` (organization + me + my roles), incl. `HasP1`.
- **Gate:** tests green; a throwaway console harness (not committed) signs in and prints `TenantContext`.

## M2 — Core: cache, sync, search
- [ ] 2.1 `Cache/CacheDb`: open/migrate from embedded `0001_init.sql` (doc 05, including FTS triggers), typed upsert methods per table, transaction helper, `Clear(entity)`.
- [ ] 2.2 🧪 `Models/*` records with `FromJson`, badge booleans, hybrid read-only property set (`DirectoryUser.IsReadOnly(propertyName)`). Tests from captured sample JSON in `tests/Fixtures/`.
- [ ] 2.3 🧪 `Sync/DeltaSyncService`: initial walk + delta tick for users, groups (with `members@delta`), Entra devices; full refresh for managed devices and SKUs; `sync_state` bookkeeping; 410 → rebuild; progress events. Tests: fake handler serving a 2-page initial walk then a delta with one update, one removal, one member add/remove → assert cache rows.
- [ ] 2.4 🧪 `Search/SearchService` per doc 03 + UX §3 ranking and prefixes. Test: seed 5,000 synthetic users/2,000 devices/1,000 groups in `:memory:`, assert p95 query time < 100 ms and ranking cases (prefix beats substring; recent boost).
- [ ] 2.5 `Cache/ActionLog` (`actions.db`) with `Record(action)` and `Query(...)`; inverse-payload storage for undo.
- **Gate:** tests green; harness runs initial sync on dev tenant and searches.

## M3 — App shell
- [ ] 3.1 `App.xaml.cs` host builder + DI per doc 03; file logger provider; global exception handler that logs and shows a non-modal error bar (never crash silently).
- [ ] 3.2 🖥 `MainWindow` as `ui:FluentWindow`: title bar with tenant name + sync status pill + account menu; 48 px nav rail; content `ContentControl` with DataTemplates; status bar. Dark theme, palette colors from UX §1 as resources (`YlNavy`, `YlCoral`).
- [ ] 3.3 🖥 First-run flow (UX §9): SignIn view → scope explanation view → sync progress view → Home. Error states listed in §9.
- [ ] 3.4 🖥 `ShellViewModel` navigation with back-stack (Alt+←), tab strip (Ctrl+T/W/Tab), keyboard shortcuts table from UX §11 via `InputBindings`.
- [ ] 3.5 🖥 Command palette overlay (UX §3): debounce, grouping, ranking display, prefixes, keyboard nav, empty/partial-index states, opens objects/actions.
- **Gate:** CI green on windows-latest; app starts, signs in, syncs, palette finds a user by partial UPN and a device by serial suffix in < 100 ms (log the timing).

## M4 — User 360 (read)
- [ ] 4.1 🖥 `UserDetailViewModel` loads from cache instantly, then hydrates live via one `$batch` (manager, memberOf, auth methods, registeredDevices, licenseAssignmentStates, signInActivity-refresh); skeletons per tab.
- [ ] 4.2 🖥 Header + badges + sidebar (UX §4). Copy-to-clipboard on mono fields with toast.
- [ ] 4.3 🖥 Identity tab read-only rendering with hybrid captions; Access tab (licenses with seat counts, groups with type badges, roles, auth methods); Devices tab cards from cache + live compliance; Activity tab (P1 degrade); Mailbox tab disabled with reason.
- [ ] 4.4 🖥 Device page (UX §5) read-only: overview, people, hardware, compliance per policy. BitLocker/LAPS reveal buttons present but grey with "not yet granted" reason.
- [ ] 4.5 🖥 Group page: header, members list (on-demand fetch, paged), owners, kind/dynamic/synced badges.
- **Gate:** open 20 different users; every field populated correctly vs the Entra portal; no UI stall.

## M5 — Writes (small, reversible)
- [ ] 5.1 Incremental consent: `EnsureWriteScopesAsync` on first write; Permissions page in Settings (UX §12); grey rule wiring for missing scopes (UX §6).
- [ ] 5.2 🖥 Identity tab per-field edit-on-blur with spinner/check, PATCH via `UserService.UpdateAsync`, write-through to cache, undo toast, error mapping (UX §7). Hybrid guard.
- [ ] 5.3 🖥 `Licensing/LicenseService`: toggle assign/remove with usageLocation pre-check and friendly SKU names; undo.
- [ ] 5.4 🖥 Group add/remove from Access tab with skip rules; undo. Group picker (palette-style, groups only).
- [ ] 5.5 🖥 Auth method delete with confirm; Revoke sessions; Reset password (dialog: generate/show-once or admin-typed; forceChange checkbox).
- [ ] 5.6 🖥 Device actions: Sync, Restart (confirm), Retire (preview dialog pattern), Wipe (typed-name double confirm). BitLocker reveal + LAPS reveal behind sensitive-scope consent, each reveal logged to ActionLog.
- **Gate:** every write reflected in the portal within 10 s; every write has an ActionLog row; undo works for the reversible ones.

## M6 — Offboarding
- [ ] 6.1 🧪 `Offboarding/IOffboardingStep`, `OffboardingContext`, `OffboardingRunner` (parallel preview, sequential execute, halt semantics, undo collection). Tests with fake steps.
- [ ] 6.2 🧪 Steps 1–9, 16, 17 from doc 06 (15 → investigate, log decision). Tests for skip rules (dynamic/synced/role-assignable groups; group-assigned licenses; on-prem-mastered attributes).
- [ ] 6.3 Profiles: load/save JSON; default "Standard leaver".
- [ ] 6.4 🖥 Offboard dialog implementing UX §8 exactly (preview → live log → summary with undo). Reachable from User 360 header, palette action `> offboard`, and Ctrl+Shift+O on a user page.
- **Gate:** offboard a test user with all default steps; verify each effect in the portal; undo the reversible set; re-run and confirm idempotent ("already disabled — nothing to do").

## M7 — Templates & create
- [ ] 7.1 🧪 `Templates/UserTemplate` model + `TemplateService` (snapshot rules, slug, load/save, rule expansion for UPN/displayName/mailNickname, password generator with the guarantee tests).
- [ ] 7.2 🖥 Templates nav page: list, create-from-user (picker), edit JSON in a monospace editor with validation, delete.
- [ ] 7.3 🖥 Create-from-template flow (doc 07 §"flow") incl. live UPN/proxyAddress uniqueness check and seat-count guards; preview → execute → result with show-once password.
- **Gate:** create 3 users from a template in < 30 s each; they appear in the palette on next delta without restart.

## M8 — Polish for dogfood
- [ ] 8.1 Settings page complete (UX §12), rebuild index, export logs.
- [ ] 8.2 Activity nav page over ActionLog with filters and copy-as-markdown.
- [ ] 8.3 Performance pass: measure cold start, palette p95, User 360 time-to-interactive on the dev tenant; record numbers in `docs/PERF.md`; fix anything over the targets in doc 01.
- [ ] 8.4 Accessibility pass: every control keyboard-reachable, AutomationProperties.Name on icon-only buttons, contrast on coral-on-navy verified ≥ 4.5:1 for text.
- [ ] 8.5 `README.md` for the repo: what it is, how to build, how to register the app, screenshots.
- **Gate:** run the app for a full workday of real helpdesk-style tasks; log every friction point into `docs/BACKLOG.md`.

## Not in Phase 1 (do not build even if tempting)
Exchange Online anything · on-prem AD · multi-tenant switcher · MSIX/installer/auto-update · licensing enforcement · telemetry · bulk CSV · reporting · scheduling · light theme polish beyond "doesn't look broken".
