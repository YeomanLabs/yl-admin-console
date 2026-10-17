# Decisions log

Append-only. Format: `YYYY-MM-DD · <area> · <decision> · <why> · <alternatives rejected>`

- 2026-09-08 · naming · Working name "YeomanLabs Admin Console", namespace `AdminConsole` · placeholder until product name chosen · —
- 2026-09-08 · Graph access · Raw HttpClient, no Microsoft.Graph SDK · control over batching/delta/throttling, smaller payloads, matches existing Invoke-MgGraphRequest habits · Graph SDK v5 (heavy, generated types fight `$select`)
- 2026-09-08 · auth · Own multi-tenant public-client app registration with incremental consent · product must be consentable by customers and pass security review; granular scopes over Directory.* · reusing Microsoft Graph CLI first-party app ID (fine for personal use, not for a product)
- 2026-09-08 · UI framework · WPF + WPF-UI 3.x · existing WPF experience, mature tooling; WinUI 3 rejected for tooling friction and unpackaged-app pain
- 2026-09-09 · test deps · FluentAssertions pinned to 7.0.0 · 8.x moved to a paid license for non-OSS use; 7.x is the last Apache-2.0 line · FluentAssertions 8.x, Shouldly
- 2026-09-09 · app registration · Created "YeomanLabs Admin Console" in the YeomanLabs Consulting dev tenant (appId 61ef07ab-e035-49e2-b05e-cfd25fc1c1fe, multi-tenant public client, broker + nativeclient + localhost redirects, 22 delegated scopes, admin consent granted) · doc 08 steps 1–5 · —
- 2026-09-09 · scopes · Dropped `Device.ReadWrite.All` from the write tier; offboarding step 9 (delete Entra device objects) deferred to Phase 2 · the scope is application-only on Graph; the only delegated route is `Directory.AccessAsUser.All`, which doc 08 bans as it grants the app the admin's full directory rights · adding `Directory.AccessAsUser.All`
- 2026-09-09 · roles · Directory role names come from a local template-ID table (`DirectoryRoleRef.DisplayNameFor`), not from Graph · `/me/memberOf/microsoft.graph.directoryRole` returns `displayName: null` unless the token has `Directory.Read.All` or `RoleManagement.Read.Directory`; `roleTemplateId` is always returned and is what the grey rule compares on · adding `RoleManagement.Read.Directory` (one more admin-consent scope for a cosmetic hint)
- 2026-09-09 · Graph selects · Removed `ownerType` from the managedDevice `$select` (doc 04) · v1.0 rejects it with "Could not find a property named ownerType on type microsoft.graph.managedDevice"; `managedDeviceOwnerType` is the real property and was already in the list, so `managed_devices.owner_type` maps from it · —
- 2026-09-09 · Graph selects · Dropped `autopilotEnrolled` and `joinType` from the managedDevice `$select`; added `enrollmentProfileName` and `azureADRegistered` · both are beta-only and v1.0 400s on them; `managed_devices.autopilot` is derived as `enrollmentProfileName != null` and join type is read from `deviceEnrollmentType` · fetching managedDevices from beta (doc 03 allows beta only where v1.0 cannot do it; the derivation is good enough for a badge)
- 2026-09-09 · throttling · Rapid back-to-back User 360 hydrations can earn a 60 s `Retry-After` from Graph · the M4 gate hydrated 11 users in ~8 s (77 inner requests) and Graph throttled one $batch; `GraphClient` honored it and the request succeeded on retry, but the page showed skeletons for a minute · no change: real use opens one user at a time, and shortening the wait would ignore the service directive. Revisit in M8.3 if a "still loading, Graph is throttling" caption is wanted (UX §7 already specifies one for exhausted retries)
- 2026-09-09 · JSON · `GraphJson.Options` uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` · the default encoder writes `+` and every non-ASCII character as a \uXXXX escape, which makes phone numbers and non-English names unreadable in the action log and in copied payloads · the escaping it skips only matters when JSON is embedded in HTML, which this app never does
- 2026-09-09 · licences · `LicenseService` re-reads the user up to 5 times (0.4/0.8/1.6/3.2 s backoff) until the licence set matches what was just written · `POST /users/{id}/assignLicense` is eventually consistent, unlike a user PATCH: a read taken immediately after it still returned the pre-write set during live M5.3 testing, so a single write-through would cache a value that is about to change · single read then wait for the delta tick (leaves the UI wrong for up to 2 minutes)
- 2026-09-09 · UX §6 · A missing directory role explains but no longer disables a control (`GreyRule.Reason`) · `PermissionResult.CanRun` is true for `AllowedWithRoleWarning` and roles cannot be enumerated with certainty, so disabling on a guess blocked admins who were in fact allowed; Graph gives the real answer and UX §7 maps the 403 · keeping the control disabled (safer-looking, but it hides a working action)
- 2026-09-09 · dialogs · Modals are an MVVM overlay (`DialogService` + `DialogOverlay`), not `Wpf.Ui.ContentDialog` · page view-models must never touch a Window, and the typed-name gate and show-once password need their own view-models anyway; one host also guarantees a single modal at a time · `ContentDialogService` (needs a presenter and code-behind per page), `MessageBox` (no styling, no UX §2 preview)
- 2026-09-09 · passwords · A reset password is returned from `ResetPasswordAsync` in a `PasswordResetResult` rather than on `WriteOutcome` · `WriteOutcome.Summary` feeds toasts and the action log, so a password on it would leak by accident; keeping it on a separate type makes the show-once dialog the only consumer · putting it on WriteOutcome behind a naming convention
- 2026-09-09 · BitLocker · The reveal sends `$select=key` and nothing else · naming any other property alongside it returns 200 with the key silently missing, and so does omitting `$select`; the response still carries id, volumeType and createdDateTime, so nothing is lost · selecting the fields we want (looks tidier, returns no key)
- 2026-09-09 · LAPS · `deviceLocalCredentials` is called on v1.0 and keyed on the Entra `deviceId` GUID · doc 04 specified the directory object id on beta; the object id answers 400 "could not be found" on both v1.0 and beta, and v1.0 answers correctly for the deviceId · following the doc (would have shipped a reveal that always failed)
- 2026-09-09 · secrets · Reveals return `RevealResult<T>`, not `WriteOutcome` · `WriteOutcome.Summary` is what toasts and the action log print, so a secret placed on it leaks by construction; a separate type makes the dialog the only consumer · reusing WriteOutcome with a naming convention
- 2026-09-09 · device actions · Restart, Retire and Wipe are built and unit-tested but were never fired at a real device · the owner's explicit instruction: they will run them when they choose. Sync and the BitLocker reveal were run live because neither changes or destroys anything · running them against a spare device (none available in the dev tenant)
- 2026-09-09 · offboarding · Doc 06's step "Capability" column is modelled as `StepSurface` (Graph / Exchange / OnPremAd), separate from `Auth.Capability` · a step needs both — which system it talks to, and which permission it requires — and reusing one name for both would have made the grey rule unreadable · overloading Capability with a surface field
- 2026-09-09 · offboarding · A preview that throws still produces a row, marked Failed with CanRun false · a step silently missing from the dialog reads as "this will not run", which is a claim nobody can make when the preview never completed (CLAUDE.md non-negotiable 2) · letting the exception fail the whole preview
- 2026-09-09 · action log · `ActionLog.BeginCorrelation` sets an AsyncLocal that `Record` stamps onto rows that carry no id of their own · offboarding steps call the ordinary write services, which know nothing about runs; threading a correlation id through every service method would touch every call site for one feature · adding a correlationId parameter to every write method
- 2026-09-09 · offboarding · Doc 06 step 15 (delegate-onedrive) is registered blocked, not built · sharing a leaver's OneDrive needs a Files.* scope; the only delegated route is `Files.ReadWrite.All`, which is not among doc 08's 22 scopes and would let the app read and write every file the admin can reach · adding Files.ReadWrite.All for one optional step, or driving the SharePoint admin API (a second surface for one step)
- 2026-09-09 · offboarding · Ownership transfer adds the delegate as owner before removing the leaver · a Microsoft 365 group with no owner is one nobody can administer, and doing it the other way round leaves that state between two calls · a single batched request (Graph has no atomic swap)
- 2026-09-09 · offboarding · Undo of the ownership transfer makes the leaver an owner again but leaves the delegate in place · removing the delegate could leave the group ownerless if the re-add failed, and doc 06 already calls this step partially reversible · a full reversal
- 2026-09-09 · Intune · `deviceManagement/managedDevices` is filtered on `userPrincipalName`, not `userId` · a userId filter answers 400 "Unsupported parameter found in query" with or without a `$select`, while the UPN filter is accepted (verified on the dev tenant) · reading every device and filtering client-side (works, but pulls the whole estate for one user)
- 2026-09-09 · errors · `GraphException` parses Intune's own error body (`{"_version":3,"Message":"…"}`) and `GraphErrorText` trims it at the support operation id · without this the whole JSON blob, including the full request URL, was what the admin read in the offboarding preview · leaving the raw body (UX §7 requires human-readable text)
- 2026-09-09 · offboarding UI · The offboard dialog previews every step, not only the ticked ones · a row with no preview shows no reason for being greyed, so the admin cannot tell a blocked step from one they simply had not ticked · previewing on demand as steps are ticked (cheaper, but the greyed rows stay unexplained)
- 2026-09-09 · templates · `NameRules` supports initials (`{f}`, `{l}`, `{F}`, `{L}`) beyond doc 07's token list · `{f}{last}@{domain}` is how a great many organisations build a UPN, and without it every created account would need hand-editing, which is the thing templates exist to avoid · sticking to the documented tokens
- 2026-09-09 · templates · An unknown or unclosed rule token is rendered as written, not as an empty string · a mistyped `{frist}` must be visible to the admin at the preview; silently dropping it produces a plausible-looking wrong UPN, and a UPN is the one field that cannot be quietly corrected afterwards · treating unknown tokens as empty
- 2026-09-09 · templates · Address rules fold accents to ASCII and drop punctuation Entra rejects; display-name rules do not · "Zoë O'Brien" must reach Graph as `zoe.obrien@…` but must still be shown as "Zoë O'Brien" · one sanitiser for all three rules (would mangle the display name)
- 2026-09-09 · templates · Template attribute values are converted from `JsonElement` to plain CLR values when a template is loaded or parsed · deserializing `object?` yields `JsonElement`, so every `value as string` read returned null for a template loaded from disk while an in-memory one worked; the M7 gate saw this as "a usage location is needed" on a template that had one · a custom converter, or teaching every reader about JsonElement
- 2026-09-09 · templates · The snapshot filters `memberOf` to groups by `@odata.type` · a `directoryRole` parses as a group perfectly happily, and adding a member to one answers 404; the M7 gate put two of the admin's directory roles into a template as groups · relying on the group-kind derivation, which cannot tell a role from a group
- 2026-09-09 · create · The licence step retries a 409 `Directory_ConcurrencyViolation` up to four times with a 1/2/3 s backoff · Entra needs a moment after a user is created before it accepts a licence for them, and its own message says to wait briefly and retry; one of the three gate creates hit it · retrying every 409 in `GraphClient` (a conflict elsewhere is usually genuine)
- 2026-09-09 · create · A uniqueness check that Graph cannot answer reports "could not check", never "available" · telling an admin a sign-in name is free when nobody looked is worse than admitting the check failed, and a UPN cannot be quietly corrected afterwards · treating an error as available (optimistic, and wrong exactly when it matters)

## M8.5 — The backlog is seeded before the workday, not after it

`docs/BACKLOG.md` is the M8 gate's artefact, and the gate has not been run. It would have been tidier to create the
file empty and let the workday fill it. It is seeded instead, with the twelve things this build already knows are
rough or unverified, because those are exactly the things a workday will not find: nobody discovers "high-contrast
mode is untested" by doing a day of tickets. Writing them down now keeps them from being re-found, and keeps the
workday's own entries — which go in a separate section at the bottom, dated — clearly distinct from what was known
before it started.

## M8.5 — The README's screenshots are of the live tenant

The four screenshots are the real app against the real dev tenant, not mockups, because a README of an admin tool that
shows invented data is a README you cannot trust. The cost is that `offboard-preview.png` shows the admin's own device
names and registered authentication methods, and `activity-log.png` shows the M7 gate's test accounts. The repository
has no remote, so nothing has left the machine — but if it is ever pushed anywhere public, those two images are what
to look at first.

## M9.2 — The list chrome is a UserControl, not a ControlTemplate

The heading, filter box, chips, empty state and paging are shared by all three list pages, so they were first written
once as a `ControlTemplate` applied to a `ContentControl`. It rendered perfectly and was invisible to a screen reader:
WPF does not create automation peers for `TextBlock`s that are template children, so the page heading, the result
count and the empty-state text were absent from the automation tree entirely — including the live region whose whole
job is to announce that a filter changed the count. The interactive controls in the same template were exposed, which
is what made it look fine until the tree was actually dumped.

It is now `Views/ListPageView.xaml`, a UserControl whose children are ordinary visual children and are exposed like
any other text. Each page passes its rows in through a `RowsContent` dependency property. Same sharing, same markup,
and the automation tree goes from 0 to 3 headings and 3 live counts.

The general lesson, worth keeping: rendering correctly is not evidence of being accessible, and the automation tree
has to be dumped to know. M8.4 audited the pages that existed then; anything built since needs the same pass.

## M9.6 — Contrast is measured from the screen, not from the palette

The M8.4 accessibility pass computed contrast from the hex values in `Theme.xaml` and reported everything passing. It
was checking the wrong things. It looked at the brand colours — coral on navy, danger on the page — and never looked at
ordinary body text, which is almost all the text in the app. Andrew, using it, said it was "insanely hard to read, all
greyscale", and he was right.

Three faults, none visible from the palette alone:

1. **`Opacity` compounds.** 139 TextBlocks dimmed themselves on top of a theme brush that was already dimmed. A
   `0.55` that measures 5.9:1 against the page lands at 4.06:1 once it multiplies with the inherited foreground, and
   the empty-filter chip reached 2.79:1 — unreadable, in order to convey "not worth reading".
2. **The library's semantic brushes do not match their names.** Switching to `TextFillColorSecondaryBrush` looked like
   the principled fix and changed almost nothing: in WPF-UI 3.0.5 it renders about #868686, and even
   `TextFillColorPrimaryBrush` came out #999999 inside a button. Its tertiary brush is 3.32:1 in the light theme.
3. **The danger colour was measured against the wrong background.** M8.4 recorded 4.63:1 for danger text on the page.
   It is used on rows, where it is 3.87:1. And as a badge fill with white text it was 3.56:1.

What settled it was sampling the pixels of an actual screenshot, which is now how this gets checked: `Shot-Launch.ps1`
captures the window untouched and `Measure-Contrast.ps1` reports the real ratio per region. Secondary text went from
4.86:1 to 8.23:1 and primary from 6.05:1 to 13.77:1.

The rule that follows: a colour is only checked against the surface it is actually drawn on, in a rendered frame. A
computed ratio from a stylesheet is a hypothesis.

## M9.6 — The palette no longer opens on top of Home

UX §9 step 4 says to land on Home with the palette open. That was written when Home was a single sentence telling the
admin to press Ctrl+K — there was nothing for the overlay to cover, and it was the only way to do anything.

Now that Home is a real page, the overlay lands across the middle of it: two search boxes stacked, the second hiding
the first and the "Start something" buttons. Andrew's words were "you have a search bar over the whole thing". It
reads as a bug even though it was the spec.

Home now carries its own search, which opens the palette and is captioned Ctrl+K, so the shortcut is taught instead of
being imposed. Deviating from the spec here is deliberate and recorded, per CLAUDE.md.

## M9 — A picker opened by a dialog sits above that dialog

Reported by Andrew: in the create-user dialog, clicking **Choose** for the manager did nothing.

The button was wired correctly. `MainWindow.xaml` declared `PaletteOverlay` before `DialogOverlay`, and later siblings
in a WPF `Grid` paint on top, so the picker opened *underneath* the dialog and its scrim — present in the automation
tree, invisible on screen, and unclickable. The comment above it said the dialog sits "above everything, including the
palette, because a confirm must be answered", which is right for a confirm and wrong when the dialog is the thing
asking for the palette.

Three buttons were dead, not one: the manager and **Add to group** in the create-user dialog, and the delegate in the
offboard dialog — all the same picker.

The rule now: the palette takes `ZIndex 2` only while `IsPicking`, so a picker a dialog opened is on top, and a plain
Ctrl+K still sits below an open dialog. The original intent survives and the picker works.

Two things worth keeping from how this was missed. The M6.4 and M7 gates both drove these dialogs — the M7 gate created
three users from a template — and neither caught it, because the template already carried a manager, so nothing ever
pressed **Choose**. A gate that walks only the path it set up will miss exactly this. And the WPF project has no unit
tests by design, so the regression check here is a driver script (`Drive-Pickers.ps1`) rather than a test; that is a
real gap, recorded in the backlog.

## M9 — The person picker takes its prompt from the caller

Once the picker was visible it turned out to be asking the wrong question. `OpenPersonPicker` hardcoded "Choose who
inherits ownership and access" — the offboarding delegate's wording — so choosing a new user's manager, or the person
to snapshot a template from, all asked about inheriting ownership. The prompt is now passed in by whoever opens it.

## M9 — Clearing a directory property sends null, never an empty string

Found by Andrew running a real offboard against a disposable test user. The stamp step cleared `officeLocation` and
`department` by sending `""`. Graph accepted the first and rejected the second with "Invalid value specified for
property 'department' of resource 'User'", in the same request sequence — so the inconsistency is Graph's, not ours.

Empty and null mean the same thing for a directory string — no value — but only `null` says so to Graph. `UpdateAsync`
now normalises an empty string to `null` before building the PATCH body, and the no-op check treats them as equal so
clearing an already-empty field still sends nothing.

Fixing it in `UserService.UpdateAsync` rather than in the offboarding step's defaults is deliberate: the same bug was
sitting under the Identity tab, where an admin clearing Department by hand would have hit the identical error.

Verified against the live tenant on the admin's own account: set to a marker, cleared with `""`, read back empty,
restored. Pinned by a test that asserts the body is `{"jobTitle":null}`.

## M9 — Row and result text is pinned to the primary brush, not inherited

The palette's result names came out darker than the sign-in names beside them — the one word you are reading, hardest
to read. Same cause as the list rows: a `TextBlock` inside a `Button` inherits the button's dimmed foreground, and
giving the *secondary* line an explicit brush had made it brighter than the primary, inverting the hierarchy.

Pinned on the row styles (`ListRowButton`, `PaletteRowButton`) rather than per-TextBlock, so it holds for anything
placed in a row later. An app-wide implicit `TextBlock` style was considered and rejected: a style setter beats an
inherited value, so it would have overridden `YlOnNavy` on the navy chrome and undone the M8.4 light-theme fix.

## M10 — A registration per tenant, not one shared multi-tenant app

Andrew asked whether the console could be made tenant-agnostic with a script doing the Entra side. It already was
tenant-agnostic where it counts — the registration is multi-tenant and the authority is the `organizations` endpoint —
so the real question was which model to set clients up with.

**Chosen: one registration per tenant, created by a script, owned by that tenant.** The alternative was every client
consenting to one multi-tenant app owned by YeomanLabs. That is less setup and it means YeomanLabs owns an object
holding `User.ReadWrite.All`, `UserAuthenticationMethod.ReadWrite.All` and `BitlockerKey.Read.All` against every
client directory at once — a standing cross-tenant blast radius, a consent screen that shows an unverified-publisher
warning until Partner Center clears, and something a decent security review should refuse. Per-tenant, revoking a
client's access is deleting one object in their own directory.

Two things followed from the choice:

The client ID could no longer live in the shipped `appsettings.json`. It is per-installation, so it belongs beside
`settings.json` in `%LOCALAPPDATA%` — the same reasoning that moved the sync and theme settings there in M8.1. An
update overwrites the install directory, and it may be read-only under Program Files.

The script and `Scopes.cs` are now two copies of the same list, which will drift. Generating one from the other would
mean either the script fetching a list from something it cannot reach, or a build step emitting PowerShell. A test
that parses the script and compares is duller and catches the same mistake — and the mistake it catches is expensive:
drift shows up as a consent failure on a client's first write, in their tenant, long after anyone would connect it to
the edit that caused it.

## M11.1 — A unit test must not be able to reach a real installation

The migration that carries a pre-rename install's data to the new folder was first written as one method,
`AppPaths.MigrateLegacyRoot()`, reading two static paths under `%LOCALAPPDATA%`. The test for it called that method.

Running the suite therefore moved the developer's own live installation — cache, saved templates, action log and MSAL
token cache — and by the end of the run the data was gone. The test failed loudly, which is the only good thing about
it; a passing version would have destroyed the same data silently on every machine that ever ran the suite, including
a client's.

The move is now `Migrate(from, to)`, an internal method taking explicit paths, with `MigrateLegacyRoot()` a one-line
wrapper the app calls. Every test works inside a temp directory, and they cover more than the original did: that
everything comes across, that an existing destination is never overwritten, and that a second run is a no-op.

The general rule this earns: a test may not call a method whose behaviour depends on an ambient machine path. If the
only way to test something is to run it for real, the paths are the wrong shape — pass them in.

## M11 — Two colours and a name, and nothing else

Branding is `productName`, `tagline`, `chromeColor`, `accentColor`. No logo slot, no font override, no per-surface
colour list.

The temptation with white-labelling is to expose everything, and every field exposed is a way for a deployment to
produce something unreadable. The contrast work in M9.6 and `docs/ACCESSIBILITY.md` only holds because the palette is
small enough to reason about: two configurable colours, and everything else derived from them.

Derived means computed, not configured. The rail is the chrome shifted towards the nearer end. Text on the chrome is
light or dark according to the chrome's own luminance — which makes the M8.4 bug *structurally impossible* rather than
merely fixed: that failure was a hardcoded light foreground meeting a panel that had gone light, and there is now no
hardcoded foreground to be wrong.

Anything unpaintable falls back to the shipped default. `branding.json` is hand-editable by design, so a typo is a
question of when, and a typo must not produce a window somebody cannot read their way out of.

## M12.4 — What the fake got right and the domain controller did differently

`FakeDirectoryService` was written to refuse what a real DC refuses, and it does. What it could not anticipate is the
opposite case: things AD reports as **errors** which are actually the state being asked for.

- **Clearing an attribute that is already absent** returns `NoSuchAttribute` (16). The fake returned a no-op. Left
  alone, "clear this field" would have failed whenever the field was already empty — which is most of the time.
- **Adding someone already in a group** returns `EntryAlreadyExists` (68). The fake returned a no-op. Left alone,
  re-running an offboarding step or clicking twice would have shown a failure for something that was true.

Both are now named result codes that `SendAsync` converts into no-ops.

The third finding is about the error text, and it is the one worth remembering. A password refused by policy comes
back as:

```
0000052D: SvcErr: DSID-031A1260, problem 5003 (WILL_NOT_PERFORM), data 0
```

The `data` field is **zero**. The code that explains it is the eight hex digits at the *front*. The parser only read
after `data `, so it fell through to the generic "the domain controller refused the operation" — which tells an admin
resetting a password nothing they can act on. Both positions are read now.

That one nearly escaped: the harness assertion was `FriendlyError.Contains("policy")`, and the *generic* message
happens to contain the word "policy" too. It passed for the wrong reason, and only reading the actual printed message
caught it. An assertion loose enough to pass on the fallback text is not testing the thing it names.

## M12.4 — a field never unlocks into a write that cannot work

`allowedAttributesEffective` is read per object and per admin before any on-prem-mastered field is offered as
editable. It answers two questions in one query — is the attribute in this forest's schema, and has this admin been
delegated it — and it is the only way to answer either without attempting the write.

The alternative was to unlock all twenty and let the failure explain itself. That breaks CLAUDE.md's second
non-negotiable in the way that annoys people most: the control accepts a value, the admin moves on, and the refusal
arrives after they have stopped looking. `mailNickname` is the concrete case — it does not exist in a forest without
the Exchange schema extension, which is most labs and some real forests.

When the directory does not answer, the set is null and the field unlocks anyway. Locking everything on the strength
of a missing answer would remove the feature from a domain where it would have worked, and the write's own refusal
still carries a real reason.

## M12.4 — LDAP result code 16 means two different things, and only the operation separates them

A real DC returns `NoSuchAttribute` both for "that attribute is already absent" and for "this schema has no such
attribute". Treating 16 as a benign no-op for every modify — which is what clearing an absent attribute needed —
reported a write that never happened as a success. The operation decides instead: a delete that returns 16 reached
the end state it wanted; a replace that returns 16 wrote nothing.

The message follows the leading Win32 code rather than the LDAP one, which is where AD actually distinguishes them:
`00000057` for a schema gap, `00002076` for an attribute simply not set on the object.

## M12.4 — a value written on-prem is held in the field until the cloud agrees

Graph keeps returning the old value until Entra Connect runs, so a refresh between the write and the sync would reset
the field and look exactly like the edit had been lost. The field holds what was written under a waiting caption
instead of a done tick, and the caption clears itself the moment the cloud reports the same value — which is doc 01's
"never wonder did that sync yet", made literal.

## M12.5 — Active Directory does not prevent circular group nesting

Tried against a real domain controller: a group added to itself, a two-group loop and a three-group loop were all
accepted, with no error and no warning. The widely repeated claim that AD blocks circular nesting is not what AD
does.

The console had been refusing all three. That is the one thing a pre-check must never be — stricter than the
directory it is a window onto. It blocks work that is permitted, and the admin has no way to tell whether the rule
was AD's or ours. Every other rule in `GroupRules` was confirmed refused by the directory before being written down;
this one was belief, and belief is what the lab is for.

It is now a warning on a write that goes through, carried on `DirectoryOutcome.Warning`. Nothing breaks — token
evaluation does not follow loops — but the nesting stops meaning anything, and it is almost always a mis-click. The
fake was enforcing the same phantom rule, which is the "a fake that says yes teaches the calling code to assume yes"
trap pointing the other way: a fake that says no teaches the calling code that no is possible.

## M12.5 — scope and category are written together, because they are one attribute

`groupType` carries both. A conversion that wrote the scope bits without carrying the category through would turn a
security group into a distribution one, which grants nothing — every permission held through that group would stop
working, silently, and be noticed days later by whoever lost access. `SetGroupTypeAsync` therefore takes both, and
the value is built from nothing each time rather than by flipping bits on the old one, since two scope bits set at
once is rejected.

## M12.6 — "protect from accidental deletion" is read from the DACL, not guessed at

There is no attribute for the checkbox. Ticking it writes a deny ACE for Everyone covering Delete and Delete Subtree,
and AD answers a blocked delete with a bare access-denied that is indistinguishable from a missing delegation. Without
reading the descriptor, the console would send an admin to audit rights that were never the problem. `CommonSecurityDescriptor`
from the BCL parses the raw bytes, so this needs no dependency on `System.DirectoryServices` and its Windows-only
object model — but the method is marked `[SupportedOSPlatform("windows")]`, as is `LdapDirectoryService` as a whole,
because both were always Windows-only and saying so lets the analyser check callers instead of warning inside.

## M12.7 — the leavers OU is configuration, and its absence blocks the step rather than defaulting

`OnPrem:DisabledOu` has no default. An OU carries policy — a restrictive GPO, a different password policy, exclusion
from sync — so a console-chosen destination would apply somebody else's rules to an account at the moment it matters
most. Unset, the step previews as blocked and names the setting.

Neither on-prem step halts the run when it fails. They are the steps that make an offboarding permanent, but stopping
there would leave licences assigned and sessions live, which is worse than carrying on and reporting loudly.

## M12.8 — a bad move destination is LDAP 80, and AD cannot say which kind of bad

Result code 80 with `00002089 ... problem 5012 (DIR_ERROR)` comes back both for a malformed distinguished name and
for a container that does not exist. Nothing in the answer distinguishes them, so the console reads the destination
before sending and says which. Code 80 is also in the table now; it was not, and both cases produced "Active
Directory returned error 80".

## M12.8 — the password-policy message names the rule nobody expects

Windows complexity rejects any password containing a three-character-or-longer run from the account name or the
display name. `GateP@ssw0rd!2026x` was refused on an account called Gate Subject: long, mixed case, digits and
symbols, refused for containing "Gate". A message listing only length, complexity and age would leave an admin
retrying longer and longer passwords.

## M12 gate — the checker uses ADSI rather than Get-AD*, and proves itself first

The plan called for `Get-AD*`. That needs RSAT installed on this machine or WinRM remoting to the DC, and both need
an elevated prompt on the user's own computer — not something to take unasked. ADSI (`System.DirectoryServices`) is a
different library from the `System.DirectoryServices.Protocols` the console is built on, in a different process and a
different language, which is what the gate is actually for: nothing checks its own work.

The checker begins by looking up an object that cannot exist and asserting it is absent. That is not ceremony: the
first version bound with `DirectoryEntry`, which does not reliably fail for a missing distinguished name, and it
reported a deleted object as present. A checker that cannot tell absent from present is worse than no checker.

## M12.9 — a Domain row resolves to the cloud object first, and falls back only when there is none

A synced user has a User 360 with licences, groups, sign-in activity and devices on it. Opening a bare attribute
sheet instead, purely because the admin arrived from the Domain list rather than the Users list, makes one person
look like two objects depending on the door used. So the row resolves through the join M12.4 proved
(`onPremisesImmutableId` for users; for computers the Entra `deviceId`, which for a hybrid join is the AD
`objectGUID`) and opens the AD page only when the cloud genuinely has nothing. The user lookup scans synced rows and
compares anchors in C# rather than adding a column and a migration: it runs once per click, not on the search path.

## M12.11 — the AD-only page is a whole page, not a stub

For someone who exists only in AD there is no User 360 to fall back to, so a fact sheet with four buttons sends the
admin back to ADUC for the fifth thing and they keep ADUC open beside the console for good. The page now carries what
ADUC's tabs carry. Two choices worth recording:

- The editable rows are the User 360 identity rows reused, with the LDAP attribute itself as each row's property
  name. On this page there is no Graph name to translate from, and inventing a second naming layer would have
  created a second place for the `country`/`co` class of bug to live.
- Membership uses one group list per page, loaded once and capped at 500, because `memberOf` holds distinguished
  names and the contract addresses groups by GUID. The alternative — a DN lookup per row — would be dozens of LDAP
  round trips to draw one list. A row past the cap keeps its name, loses its remove button, and says why, which is
  the honest version of a limit rather than a silent one. Worth revisiting for a large domain.

`ResetComputerAccountAsync` writes `unicodePwd` as the lower-case machine name, which is exactly what ADUC's "Reset
Account" does. It is behind a confirm that names the consequence — the machine's secure channel dies until it
rejoins — because that is the part nobody knows the first time.

## Phase 3 — Exchange Online and SharePoint become allowed hosts (2026-10-17, Andrew)

Graph cannot administer somebody else's mailbox under delegated permissions. Delegation, forwarding, automatic
replies, holds, hiding from address lists and conversion to shared all belong to Exchange, and the delegated
`MailboxSettings.*` scopes reach only mailboxes the admin can already open. So staying Graph-only would leave the
Mailbox tab greyed permanently, and leave five offboarding steps (convert to shared among them) needing a second tool.
That is the gap both reference points for Phase 3 show: EasyEntra manages the mailbox from the same screen as the
account, and the scripted deprovisioning chain this was compared against ends by converting the mailbox to shared.

**Decision:** Exchange Online's admin endpoint, `outlook.office365.com`, with the delegated `Exchange.Manage`
permission, joins Graph as an allowed host. For OneDrive, the tenant's SharePoint admin endpoint joins as well, with
`Files.Read.All` on Graph. CLAUDE.md non-negotiable 3 now names these hosts. Non-negotiables 4 and 5 are unchanged in
spirit and now say so explicitly: the console still signs in as the admin, and Exchange's RBAC and SharePoint's admin
roles enforce what that person holds, the way AD enforces delegation on-prem.

Rejected:
- **Staying Graph-only.** Keeps the posture smaller, and keeps the console unable to finish an offboarding.
- **App-only certificate authentication**, as Exchange automation usually runs. A certificate with standing rights
  over every mailbox in the tenant is the thing non-negotiable 4 exists to prevent.
- **Hosting the ExchangeOnlineManagement module in-process.** A heavy client prerequisite, and errors that arrive as
  parsed text: the same reasons doc 11 rejected shelling out to RSAT for AD. The REST endpoint the module itself calls
  is the route, and M13.2 proves it before anything depends on it. If it cannot be made to work, doc 13 records the
  fallback and the decision comes back here.

This supersedes the 2026-09-09 entry that left offboarding step 15 blocked rather than add a SharePoint surface for one
step: with the OneDrive tab and export in M16, it is no longer one step.

The one-button Entra Connect delta sync was considered and **deferred**. It needs WinRM to the sync server and the
admin in `ADSyncOperators`, which is a remote-execution surface rather than another API. Hybrid onboarding (M17) waits
for the scheduled cycle instead, and says when the next one is due.

## 2026-10-17 — a delta link older than 30 days is a resync, recognised by its message

Graph has two ways of saying a stored delta link is unusable. The documented one is 410 / `syncStateNotFound`, which the
sync already handled by clearing the entity and walking it again. The other is what a link older than 30 days gets:
400 `Request_UnsupportedQuery` with the message "DeltaLink older than 30 days is not supported." Nothing else in the
response distinguishes it from a genuinely bad query, so the sync treats a 400 as a resync only when that message is
present. Keying on the error code alone would turn a real query bug (a mistyped `$select`) into a silent rebuild every
cycle; keying on nothing left an install that sat unused for a month unable ever to refresh, which is what the dev
install was until today. A proactive check on `last_delta_sync` was considered and not added: it would duplicate the
service's own rule, and the service is the authority on when a link expires.

## 2026-10-17 — M14: what the mailbox writes rely on, and what they deliberately do not do

- **Address uniqueness is Exchange's to enforce, not a pre-check's.** Plan item 14.1 said to check an address is free
  before adding it, by M7's rule. Exchange enforces uniqueness atomically at the write and its refusal
  (`ProxyAddressExistsException`, 409) names the recipient that holds the address, which the console turns into a
  sentence. A separate check would be a race and an extra round trip, and could only ever agree with the write.
  `CheckAddressAsync` exists, with M7's three answers, for anything that needs to know before it writes.
- **Synced mailboxes: the AD-mastered settings are greyed, not routed to AD yet.** Doc 13 §3 says addresses, hiding,
  Send on Behalf and forwarding on a synced mailbox belong in AD. Building that path needs an Exchange schema in the
  forest to test against, and neither YeomanLabs (no synced mailbox) nor the lab (no Exchange schema) has one, so it
  would ship unproven. Those controls say why they are unavailable instead. Logged in the backlog.
- **One write at a time per mailbox, 15 s apart, confirmed by two reads.** See doc 13 §5 for the measurements.
- **Undo of a Full Access removal restores it with automapping on.** `Get-MailboxPermission` does not report whether
  the grant was automapped, so the undo uses Exchange's default rather than guessing.
- **Archive, auto-expanding archive and folder renaming are not undoable**, and their confirms say so: disabling an
  archive starts a 30-day countdown to deleting it, auto-expanding cannot be turned off, and renamed folders are not
  renamed back.

## 2026-10-17 — M15: what the finished offboarding does, and what it deliberately leaves alone

- **The snapshot is required, not just halting.** Halting alone was not enough: the dialog drops a step whose preview
  is blocked from the run, so a failed snapshot preview would have been quietly left out and everything else run. A
  required step cannot be unticked, and while its preview is failed Run is disabled with the reason.
- **The run uses the context its preview filled.** The dialog used to build a new context for the run, which left
  every step that acts on what its preview found with nothing to act on. Found while wiring the snapshot; it predates
  M15 and no gate caught it, because the gates drove the runner directly with one context.
- **Built-in AD groups are not skipped.** The plan said "primary and protected groups skipped". The primary group
  cannot be removed through a membership write at all and is named as staying. Built-in groups such as Domain Admins
  are the memberships a leaver most needs to lose; they are marked as needing a domain admin, and a refusal fails that
  group only.
- **"Remove mailbox permissions" means the leaver's access to other mailboxes.** Send As comes from one tenant-wide
  `Get-RecipientPermission -Trustee`; Full Access has no tenant-wide read, so it is checked on the cached shared
  mailboxes, at most 100, four at a time, and the preview says how many were checked. User mailboxes delegated to an
  assistant are not scanned (BACKLOG).
- **Inbox rules are turned off, not deleted, and only the ones that send mail elsewhere.** Undo and Restore can turn
  them back on; rules that file or flag mail keep running for whoever inherits the mailbox.
- **Irreversible or outward-facing Exchange steps are off by default:** cancelling meetings (attendees are told),
  litigation hold (a legal decision), auto-reply, forwarding and delegation (they need a delegate). Removing phones is
  on: it wipes nothing, and a disabled account cannot sync anyway.
- **Forwarding with no delegate stops forwarding to an outside address** instead of being blocked, because an external
  forward is how a leaver takes mail along.
- **The auto-reply text comes from the profile (`options.autoReplyMessage`) or a template naming the delegate**, shown
  word for word in the preview. There is no editor in the dialog yet (BACKLOG).
- **Graph membership changes skip distribution lists and mail-enabled security groups** everywhere (group page,
  templates, offboarding), with the reason. Graph refuses them with a 400; Exchange is the only route.

## 2026-10-17 — M15 gate: how a gate leaver gets a mailbox without a licence

YeomanLabs has no spare Exchange-capable seat, and buying one for a test is not this project's call. The gate leaver's
mailbox is created shared and converted to a user mailbox, which Exchange allows unlicensed for 30 days; between gates
it is parked disabled and shared again so no clock runs. What this cannot exercise is the licence step itself
(`remove-licenses` previews "No licences are assigned"); that step is unchanged since M6 and the mailbox steps' order
ahead of it is pinned by a unit test. The AD half waits for the lab DC.

