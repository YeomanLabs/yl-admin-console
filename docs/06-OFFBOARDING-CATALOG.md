# 06 — Offboarding step catalog

Rewritten for M15, when the chain was finished end to end. The code is the source of truth for order and defaults
(`Offboarding/Steps/OffboardingCatalog.cs`, `Offboarding/Restore/RestoreCatalog.cs`); this document says why, and a
test (`Catalog_RunsSnapshotFirst_AndTheMailboxBeforeTheLicences_WithUniqueIds`) fails if the two drift apart.

## Runner contract

```csharp
public interface IOffboardingStep
{
    string Id { get; }                     // stable kebab-case; saved templates refer to it
    string Title { get; }
    StepSurface Surface { get; }           // Graph | Exchange | OnPremAd — what it talks to
    Capability? RequiredCapability { get; } // the permission, for the grey rule and incremental consent
    bool DefaultEnabled { get; }
    bool HaltsOnFailure { get; }           // a failure stops every later step
    bool IsRequired => false;              // cannot be unticked; a failed preview disables Run
    Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct);
    Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct);
}
```

- **Previews run in parallel; execution is sequential, in catalog order.** A preview names the real objects ("Remove
  from 3 groups: Sales, …") and never writes. It leaves what it found in the context; the execute acts on exactly
  that, so the run changes what the admin reviewed and nothing else. **The run uses the context its preview filled**
  (fixed in M15 — the dialog used to start the run with a fresh one).
- `OffboardingContext` carries the user (read fresh from Graph), the tenant, the delegate, the correlation id every
  action-log row of the run shares, the template's attribute stamps and auto-reply text, and — for a restore — the
  snapshot and target OU. Its scratch dictionary is locked: previews run at once. `GetOrAdd` lets eleven Exchange
  steps share one mailbox read.
- A step whose preview fails and that `HaltsOnFailure` halts the run before it starts. A required step whose
  preview fails disables Run with the reason.
- Each step's undo is offered once from the summary. Steps that are not reversible say so in their preview.

## The offboarding catalog

Surface: **G** Graph, **X** Exchange Online, **AD** on-prem Active Directory. Default: ✅ ticked in the shipped
"Standard leaver" template. Undo: what the summary's Undo, and Restore, can put back.

| # | Id | What it does | Surface | Default | Halts | Undo |
|---|----|--------------|---------|---------|-------|------|
| 0 | `snapshot` | Records groups, licences, manager, stampable attributes, the AD object (OU, groups) and the mailbox (type, hiding, forwarding, hold, Full Access, Send As, enabled rules) to `%LOCALAPPDATA%\AdminConsole\snapshots\{tenant}\{user}_{time}.json`, and the action log. **Required.** AD and Exchange are recorded when reachable and named as gaps when not. | G | ✅ | ✅ | — (it is what Restore uses) |
| 1 | `disable-account` | `accountEnabled=false`. On a synced account it warns that Entra Connect reverts it unless AD is disabled too (step 19). | G | ✅ | ✅ | ✅ |
| 2 | `revoke-sessions` | `revokeSignInSessions`. | G | ✅ | ✗ | ✗ |
| 3 | `reset-password` | 24 random characters, change at next sign-in, never shown or logged. Skipped with the reason when the tenant has no password writeback for a synced account. | G | ✅ | ✗ | ✗ |
| 3a | `reset-ad-password` | The same, in AD, through the admin's own bind — what step 3 cannot do for a synced account without writeback. A fresh password per account: a shared "leaver password" would make every disabled account one secret away from use. | AD | ✅ | ✗ | ✗ |
| 4 | `remove-auth-methods` | Deletes every deletable authentication method. | G | ✅ | ✗ | ✗ |
| 5 | `remove-manager` | Clears the manager. Skipped when mastered in AD. | G | ✗ | ✗ | ✅ |
| 6 | `remove-group-memberships` | Cloud groups. Dynamic, synced, role-assignable (without the role) and **distribution lists / mail-enabled security groups** (Graph refuses those) are listed as skipped with the reason. | G | ✅ | ✗ | ✅ |
| 6a | `remove-ad-group-memberships` | The synced groups step 6 skips. Built-in groups are included and marked (only a domain admin can change them); the primary group is named as staying. | AD | ✅ | ✗ | ✅ |
| 6b | `remove-from-distribution-lists` | The lists step 6 skips, through Exchange. Lists synced from AD are left to step 6a. | X | ✅ | ✗ | ✅ |
| 10 | `hide-from-gal` | Hides from address lists. Blocked on a synced mailbox (AD-mastered). | X | ✅ | ✗ | ✅ |
| 11 | `convert-to-shared` | User mailbox → shared. Warns when the shared mailbox will still need a licence (over 50 GB, archive, hold) and, for hybrid, to set the on-prem side too. | X | ✅ | ✗ | ✅ |
| 11a | `litigation-hold` | Indefinite hold. A legal decision, so off by default. | X | ✗ | ✗ | ✅ |
| 12 | `set-auto-reply` | Internal and external reply; the template's `autoReplyMessage` with its placeholders filled in, or the standard text naming the delegate, shown word for word in the preview. Blocked while the text names a delegate and none is chosen. | X | ✗ | ✗ | ✅ |
| 13 | `forward-mail` | Forwards to the delegate, keeping a copy. With no delegate, stops forwarding to an outside address. Blocked on a synced mailbox. | X | ✗ | ✗ | ✅ |
| 14 | `delegate-mailbox` | Full Access for the delegate, automapped. | X | ✗ | ✗ | ✅ |
| 14a | `remove-mailbox-permissions` | The leaver's access to *other* mailboxes: Send As anywhere (one tenant-wide read), Full Access on up to 100 cached shared mailboxes. | X | ✅ | ✗ | ✅ |
| 14b | `remove-inbox-rules` | Turns off (not deletes) the rules that forward, redirect or forward as attachment. Rules that only file mail keep running. | X | ✅ | ✗ | ✅ |
| 14c | `cancel-organized-meetings` | Cancels the meetings they organise from today on; attendees are told. Previewed with `-PreviewOnly`. | X | ✗ | ✗ | ✗ |
| 14d | `remove-mobile-devices` | Removes phone partnerships with the mailbox; nothing is wiped. | X | ✅ | ✗ | ✗ |
| 7 | `remove-licenses` | All direct licences; group-assigned ones listed as inherited. **After the mailbox steps:** a mailbox converted or held after losing its licence has already started its 30-day countdown to deletion. | G | ✅ | ✗ | ✅ |
| 8 | `retire-devices` | Retires Intune devices where the user is primary. Off: many organisations reassign hardware. | G | ✗ | ✗ | ✗ |
| 9 | `remove-registered-devices` | **Not built.** Deleting Entra device objects needs `Directory.AccessAsUser.All`, which the app does not request. | G | ✗ | — | — |
| 15 | `delegate-onedrive` | **M16.** Needs the SharePoint admin host. | G | ✗ | — | — |
| 16 | `remove-teams-ownership` | Where the user is sole owner, makes the delegate owner. | G | ✗ | ✗ | partial |
| 17 | `update-attributes` | Stamps `employeeType=Offboarded` and clears office and department (template-configurable, from the attributes the snapshot keeps). Skipped when mastered in AD. | G | ✗ | ✗ | ✅ |
| 18 | `move-ou` | Moves the AD object to the Disabled OU, before the disable. | AD | ✅ | ✗ | ✅ |
| 19 | `disable-ad-account` | Disables in AD — the disable that survives the next sync. | AD | ✅ | ✗ | ✅ |

The numbers are doc 06's original ones; the lettered steps arrived in M15. Every Exchange step needs the
Exchange Online permission and shares one rule for a synced mailbox: the settings AD masters (addresses, hiding,
Send on Behalf, forwarding) are blocked with the reason, the rest run.

## The restore catalog (M15.4)

Restore is an offboarding in reverse, through the same runner and dialog: pick a snapshot (newest first) and, for
AD, an OU (default: where the snapshot found it). Each step compares the snapshot with the account now and puts back
only the difference, so a second restore is all no-ops. All are ticked by default; none halts.

| Id | What it puts back | Surface |
|----|-------------------|---------|
| `restore-ad-ou` | The OU — before the enable, so the account never comes back to life in the disabled OU | AD |
| `restore-ad-account` | Enabled in AD | AD |
| `restore-ad-groups` | AD group memberships | AD |
| `restore-sign-in` | `accountEnabled=true` | G |
| `restore-licences` | Direct licences, warning when no seat is free | G |
| `restore-mailbox-type` | Shared → user mailbox, after the licences | X |
| `restore-groups` | Cloud groups; synced and dynamic ones are named as coming back with their masters | G |
| `restore-distribution-lists` | Distribution lists, through Exchange | X |
| `restore-manager` | The manager, for a cloud account | G |
| `restore-attributes` | The stampable attributes, for a cloud account | G |
| `restore-mailbox-settings` | Shown in address lists, forwarding, auto-reply off, rules back on, Full Access granted since removed and recorded Full Access restored. Litigation hold is never lifted. | X |
| `restore-other-mailbox-access` | Send As and Full Access on other mailboxes, from the run's own action-log rows (the snapshot cannot hold them) | X |
| `restore-not-restorable` | Not a step: says what cannot come back — the password (set one or issue a Temporary Access Pass), MFA methods, retired devices, removed phones, cancelled meetings, transferred ownership | — |

## Offboarding templates

What a kind of leaver gets: which steps are ticked when the dialog opens, and the options they run with. Made and
changed on **Templates → Offboarding** (a form: every value is a choice from a fixed set), or saved from the dialog's
ticks with **Save as template**. They are JSON under `templates\offboarding\*.json`, shared by every tenant on the
machine, since they name steps and options and never a tenant's objects. The code and the file format still call them
profiles (`OffboardingProfile`), which they were called until the Templates page had a tab for them.

```json
{ "name": "Contractor leaver",
  "description": "Contractors: reply and stamp, keep the licences for finance",
  "isDefault": true,
  "steps": { "disable-account": true, "remove-licenses": false, "set-auto-reply": true },
  "options": {
    "delegateToManager": true,
    "attributeStamps": { "employeeType": "Contractor (left)", "officeLocation": "", "department": "" },
    "autoReplyMessage": "{firstName} has left {company}. Please write to {delegate} at {delegateEmail}.",
    "exportFolder": null } }
```

- **Steps.** A step id the template does not mention takes the step's default, so a template saved before a step
  existed picks it up at its default. The snapshot cannot be turned off; a step that is not built cannot be ticked.
- **`isDefault`.** The dialog opens with the template marked default, else "Standard leaver", which is built in,
  read-only and never written to disk. Marking one clears the mark from the rest.
- **`delegateToManager`.** The dialog fills the delegate with the leaver's manager, read live. With no manager, or
  when it cannot be read, it says so and leaves the delegate empty. A delegate the admin picks or clears in the dialog
  is never replaced by a template change.
- **`attributeStamps`.** Attribute to value; an empty string clears it. Only `employeeType`, `department`,
  `officeLocation`, `jobTitle` and `companyName`: the snapshot records exactly these, so Restore can put back anything
  a template stamps. An empty set means doc 06's defaults (above).
- **`autoReplyMessage`.** Plain text; line breaks are kept. Placeholders, matched without regard to case: `{name}`,
  `{firstName}`, `{email}`, `{delegate}`, `{delegateEmail}`, `{company}` (the tenant's name). Anything else in braces
  is refused when the template is saved. A text that names the delegate blocks the step until one is chosen. Null is
  the standard text.
- **`exportFolder`.** Where `export-onedrive` writes its ZIP; a folder that does not exist on this machine blocks it.

A template's name must be unique, ignoring case, and cannot be "Standard leaver". A new template gets a file of its
own (`contractor-2.json` if the name's file is taken); a renamed one keeps its file. Saving from the dialog under the
name of an existing template updates that template rather than making a second one.

In the dialog, changing the template previews again: the reply text, the stamps and the export folder are part of
what the run acts on, and the run acts on what was last previewed. Offboarding a list opens each person on the
template the previous person ran with.

## Offboard a list (M15.5)

Paste leavers, one per line. Exact matches only — sign-in name, mail, or a display name nobody else has; a shared
name or a near miss waits for the admin to pick or skip. Then each person gets the ordinary dialog in turn, titled
"2 of 5"; closing one without running stops the list and the status bar names who was not reached.

Or import the list from a CSV ("Import CSV…"). The people column is taken by its header — the first of
UserPrincipalName, UPN, SignInName, Email, Mail, PrimarySmtpAddress, DisplayName, Name — or, when no header is one of
those, the admin chooses it. The dialog names the file, the column and how many rows it read; the values fill the list
and meet the same exact-match rule. A file never skips a preview: there is no unattended mode.

## Preview text (must be this concrete)

- "Disable sign-in for **jdoe@contoso.com** (currently enabled)"
- "Remove from 14 groups · 2 skipped: **All-Staff-Dynamic** (dynamic), **Finance-Users** (synced from AD)"
- "Remove 3 licenses: Microsoft 365 E3, Visio Plan 2, Power BI Pro · 1 inherited from group **Lic-M365-E3**"
- "Convert jdoe@contoso.com to a shared mailbox (61.5 GB)" — *a shared mailbox over 50 GB still needs a licence*
- "Turn off 1 inbox rule that sends mail elsewhere: **Forward to gmail**"
- "Move jdoe from Lab / Disabled to Lab / Users" (restore)
