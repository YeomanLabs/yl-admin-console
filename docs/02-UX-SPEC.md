# 02 — UX Spec

## 1. Visual system
- Theme: dark by default, Fluent (WPF-UI). Mica/acrylic off (performance, and it looks wrong on RDP).
- Palette: background `#041635` (navy) as the window base with WPF-UI dark neutrals for surfaces; accent `#FF6950` (coral) for primary actions, focus rings, and the active nav item only. Never use coral for destructive actions — those use the standard red.
- Type: Segoe UI Variable. Monospace (Cascadia Mono) for IDs, UPNs, serials, and logs.
- Density: compact. 32 px row height in lists, 8 px grid.
- Icons: Fluent System Icons via WPF-UI `SymbolIcon`.
- Motion: minimal. 120 ms fades on panel swaps. No animation on lists.

## 2. Shell layout
```
┌─────────────────────────────────────────────────────────┐
│ TitleBar   [tenant name ▾]        [sync status]  [user] │
├────────┬────────────────────────────────────────────────┤
│ Nav    │  Content region                                │
│ rail   │                                                │
│ 48 px  │                                                │
│        │                                                │
├────────┴────────────────────────────────────────────────┤
│ Status bar: cache age · last delta · errors · progress  │
└─────────────────────────────────────────────────────────┘
```
- Nav rail items (Phase 1): Home, Users, Groups, Devices, Templates, Activity log. Settings at the bottom.
- **Ctrl+K** anywhere opens the command palette as an overlay centered at 20% from top, 720 px wide.
- **Esc** closes the palette / current dialog. **Alt+←** goes back in the content region history.

## 3. Command palette (the home screen)
Behavior:
- Opens focused. Typing filters immediately (no Enter). Debounce 40 ms.
- Results grouped: **Users**, **Devices**, **Groups**, **Actions**. Max 8 per group, groups collapse when empty.
- Matching fields: users — displayName, UPN, mail, employeeId, jobTitle, department, mobile/business phone (digits-only match); devices — deviceName, serialNumber, model, primary user displayName; groups — displayName, mail, description; actions — command names and aliases (`"offboard"`, `"decom"`, `"new user"`, `"sync now"`).
- Ranking: prefix match on displayName > prefix on UPN/deviceName > word-prefix > substring. Recently opened objects get a boost. FTS5 with `bm25`, then re-rank client-side.
- Row layout: icon · primary (displayName) · secondary (UPN / serial · model / member count) · right-aligned badges (Disabled, Non-compliant, Synced, Guest).
- Enter opens the top result. **Ctrl+Enter** opens in a new tab. Arrows move selection.
- Prefixes narrow scope: `u:` users, `d:` devices, `g:` groups, `>` actions.
- Empty state: "Type to search 4,812 users, 2,104 devices, 943 groups · last synced 2 min ago". If the initial sync hasn't finished: "Building local index… 62% · results may be incomplete" and search still works on what's indexed.

## 4. User 360 (the main object page)
Header: avatar (initials), displayName, UPN (mono, click to copy), badges: Enabled/Disabled · Cloud-only/Synced · Member/Guest · Licensed/Unlicensed · MFA registered/not. Right side: primary action buttons: **Reset password**, **Revoke sessions**, **Offboard…**, overflow menu.

Tabs (lazy-loaded, each shows a skeleton until data arrives):
1. **Identity** — editable property sheet: name fields, job title, department, company, office, manager (picker), phones, address, employeeId, usageLocation. Hybrid rule applies (see §6). Save is per-field on blur with an inline spinner → check; no "Save" button. Undo toast for 8 s.
2. **Access** — Licenses (checkbox list of tenant SKUs with available/total counts; toggling assigns/removes immediately with undo). Groups (list with type badge: Security / M365 / Dynamic / Synced; remove-X on removable rows; "Add to group…" picker). Directory roles (read-only list). Authentication methods (list with type and detail, e.g. "Authenticator · iPhone", "Phone · +1 ***-***-4412"; delete-X on each; last MFA registration date).
3. **Devices** — cards for each Intune managed device where this user is primary or registered: name, model, OS/version, compliance badge, encryption badge, last check-in relative time, Autopilot enrolled badge. Click → Device page. Actions per card: Sync, Restart, Retire (with preview), Collect diagnostics.
4. **Activity** — last interactive and non-interactive sign-in (from signInActivity), account created, last password change, recent audit events for this user (last 20, from directoryAudits filtered by target). If the tenant lacks P1: InfoBar "Sign-in activity requires Entra ID P1".
5. **Mailbox** — Phase 3, read-only since M13.5 (doc 13 §2): loaded on first selection; sections load and fail independently; says in words when Exchange is not granted, when there is no mailbox, and when it lives elsewhere.

Sidebar (right, 280 px, collapsible): quick facts — object ID (mono, copy), created, last sync (if synced), source anchor, on-prem DN (if synced), manager, direct reports count.

## 5. Device page
Header: deviceName, model, serial (mono, copy), badges: Compliant/Non-compliant/Grace, Encrypted, Autopilot, Co-managed, ownership (Corporate/Personal). Primary actions: **Sync**, **Restart**, **Retire…**, **Wipe…** (double confirm, typed device name).
Sections: Overview (OS, version, enrollment date, last check-in, management agent, join type, Entra device ID with link to the Entra device object), People (primary user card → User 360; other registered users), Hardware (manufacturer, model, serial, storage, RAM, MAC/IP if present), Security (BitLocker recovery keys — reveal-on-click, copy, logged; LAPS password — reveal-on-click, shows expiry, logged), Compliance (per-policy state with reason on failure).

## 6. "Explain the grey" — disabled-state rules
Every disabled control has a tooltip **and** a small inline caption below/next to it. Standard reasons:
| Situation | Caption |
|---|---|
| Attribute mastered on-prem | "Mastered in on-prem AD — edit in AD until Phase 2" |
| Missing delegated scope | "Needs the '{Scope}' permission — click to grant" (opens incremental consent) |
| Missing directory role (403 after consent) | "Your account lacks a role that allows this ({role hint})" |
| Feature needs license | "Requires {license} on this tenant" |
| Dynamic group | "Membership is rule-based — edit the rule instead" |
| Group synced from on-prem | "Group is synced from on-prem AD" |
| Guest user | "Not applicable to guest accounts" |
| Exchange-dependent | "Requires Exchange Online integration (Phase 3)" |

## 7. Errors
- Graph error → friendly text map (extend as encountered):
  - `Request_ResourceNotFound` → "That object no longer exists. The cache will update on next sync."
  - `Authorization_RequestDenied` → "You don't have permission for this. {role hint if known}"
  - `Request_BadRequest` with `usageLocation` in message → "Set a usage location before assigning a license."
  - `Request_BadRequest` with `no available licenses` / `CountViolation` → "No seats left for {SKU}."
  - 429 / 503 → handled silently by retry; surface only if retries exhausted: "Microsoft Graph is throttling requests. Retrying in {n} s."
  - Network failure → "Can't reach Microsoft Graph. Working from cached data (age {n})."
- Every error surface has an expander "Details" showing status, error code, request-id, timestamp, raw message. Copy button copies all as JSON.
- Errors never use modal dialogs except when they interrupt a multi-step action (offboarding, create).

## 8. Action preview & execution pattern
Used by Offboard, Retire, Wipe, Create-from-template, bulk license change.
1. **Preview dialog**: title, target object summary, ordered checklist of steps with toggles (defaults from catalog), each step shows *what will change* in concrete terms ("Remove 3 licenses: M365 E3, Visio Plan 2, Power BI Pro"; "Remove from 14 groups (2 skipped: dynamic)"). Steps that can't run show why (grey rule). Footer: "Run 11 steps" primary button, "Cancel".
2. **Execution view** (same dialog transforms): step list becomes a live log; each row: spinner → ✓ / ✗ / ⊘ skipped with duration and a one-line result. Failures do not stop later steps unless the step is marked `HaltsOnFailure`. Cancel button aborts before the next step.
3. **Summary**: "9 completed, 1 failed, 1 skipped · 41 s". Failed rows have "Retry" and "Details". "Copy log" copies markdown. "Undo available" lists the reversible steps with an Undo button (re-enable account, restore licenses, re-add groups).

## 9. First-run
1. Splash → "Sign in with Microsoft" (WAM). 
2. Consent screen appears only if the tenant hasn't consented. Before launching the browser/WAM prompt we show our own page listing each requested scope in plain English and why ("Read all users — to build the local search index"). Read-only scopes only at first run.
3. "Preparing your tenant…" progress: org info → users → groups → devices → Intune devices, with counts ticking up. The palette becomes usable once users are done; the rest continues in the background.
4. Land on Home with the palette open.

Sign-in failure states: no account picked → back to step 1; consent declined → InfoBar explaining the app can't function without read access, retry button; conditional access block → show the CA error message from MSAL verbatim plus "Contact your Entra admin".

## 10. Toasts & undo
- Bottom-right, stack up to 3, 8 s auto-dismiss (pause on hover).
- Success toasts for every write: "Removed Visio Plan 2 from Jane Doe · Undo".
- Undo re-issues the inverse operation and shows its own toast.

## 11. Keyboard
Ctrl+K palette · Ctrl+N new user from template · Ctrl+T new tab · Ctrl+W close tab · Ctrl+Tab next tab · F5 refresh current object from Graph · Ctrl+Shift+S force delta sync · Ctrl+, settings · Alt+1..6 nav items.

## 12. Settings
Account (signed-in user, sign out, clear token cache) · Sync (delta interval, device refresh interval, rebuild index) · Appearance (theme: Dark/Light/System; density) · Permissions (list of granted scopes with "Grant" buttons for write scopes not yet consented) · Diagnostics (log folder, cache path/size, export logs).
