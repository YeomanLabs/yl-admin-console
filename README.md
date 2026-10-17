# Admin Console

<img src="docs/images/app-icon.png" width="96" align="right" alt="" />

A Windows desktop console that puts Entra ID, Intune, Exchange Online, OneDrive and on-prem Active Directory behind one
search box.

Open it, type part of a name, and you are on the person or the machine. Everything about them is on one screen: who
they are, what they can sign in with, what they are licensed for, which groups they are in, their mailbox, their
OneDrive, their AD account, and which devices are theirs. The things a helpdesk does all day — reset a password, issue
a Temporary Access Pass, fix a licence, share a mailbox, offboard a leaver, create a starter in AD from a template — are
one click from there instead of five admin centres.

![Home](docs/images/home.png)

## What it does

**Find and browse**

- **Search that answers instantly.** Everything is indexed locally, so the palette never waits on Graph. At 5,000 users,
  2,000 devices and 1,000 groups it answers in a median of 1.1 ms — see [PERF.md](docs/PERF.md).
- **Lists you can actually browse.** Users, groups and devices, sortable and filterable from the same local cache, with
  filters phrased as the questions a helpdesk asks. Every count on Home is clickable.
- **Field queries.** Type `dep=sales lic=e3 disabled=true` in a list's filter box. Each key is a fixed query in code,
  values are bound rather than pasted in, and a key the list does not know is said rather than ignored.
- **Columns and export.** Choose the columns, then export every matching row — not just the page on screen — as CSV,
  with the filters in the file name and a guard against spreadsheet formulas in display names.

**One page per person**

- **Identity, access, devices, activity, mailbox and OneDrive**, editable in place. On a synced account, the fields
  mastered on-prem are written to AD itself, through the signed-in admin's own delegation.
- **Mailbox.** Addresses, forwarding, automatic replies, regional settings, Full Access, Send As and Send on Behalf,
  conversion to shared — through Exchange Online's own admin API, as the admin.
- **OneDrive.** Lock state, sharing, site admins, and a full export of the drive to a ZIP that resumes if interrupted
  and checks every file's hash.
- **Temporary Access Pass**, issued with a lifetime and a start day, shown once and logged nowhere.

**Change many things at once**

- **Bulk edit.** Tick people (or select every match), set the fields, read a per-person preview of what changes and
  whether it is written in Entra ID or in AD, and watch each person's result arrive. One correlation id for the run.
- **Offboarding as a checklist.** A snapshot of everything first, then the AD, Graph, Exchange and OneDrive steps,
  previewed in parallel and run in order with a live log. Restore from the snapshot puts it back. A list of leavers,
  pasted or imported from CSV, can be offboarded one after another.
- **Users from templates — in AD when the organisation is synced.** The account is created in AD, and the cloud half
  (licences, groups, mailbox settings) finishes on its own once Entra Connect brings it up.

**Know where things stand**

- **Reports.** MFA readiness (who can do MFA, who is phone-only, who has nothing, who is exempt), licence usage (seats,
  direct versus group assignment, licensed accounts that are blocked or quiet for 90 days), and mailbox size and
  delegation. Each is built when you ask, kept with its date, and exported as shown.
- **My roles.** Eligible PIM roles and self-activation within each role's policy. A permission the admin lacks but is
  eligible for says so, with Activate beside it.
- **A written record.** Every write lands in a local action log with what was sent, what came back, and whether it was
  undone. The Activity page filters it and copies it as markdown.

**Several tenants.** The tenant name in the title bar is a switcher. Each tenant opens in its own tabs, with its own
sign-in, cache and sync; a tab can only ever reach the tenant it was opened in.

![The Users list](docs/images/users.png)

![User 360](docs/images/user-360.png)

## What it deliberately does not do

No telemetry. No licence server. No crash reporting. The only outbound calls are to Microsoft, for your tenant:
`login.microsoftonline.com`, `graph.microsoft.com`, Exchange Online's admin endpoint `outlook.office365.com`, and your
tenant's own SharePoint hosts. On-prem, it talks LDAP to your domain controllers. Nothing about your tenant leaves your
machine.

No service accounts and no application permissions — not for Exchange, not for SharePoint. It signs in as you, through
the Windows broker, with delegated permissions only, and asks for write permission the first time you change
something rather than up front. On-prem, AD enforces what you have been delegated, because the bind is your own.

## Install

Download the MSI for your machine from the [releases](https://github.com/YeomanLabs/yl-admin-console/releases) — `x64`
for most PCs, `arm64` for Windows on Arm. It is self-contained: no .NET runtime to install first.

```powershell
msiexec /i AdminConsole-0.3.2-x64.msi /qn      # silent, e.g. as an Intune Win32 app (installs per machine)
```

It installs to `Program Files\Admin Console` with a Start menu entry, upgrades in place, and closes a running console
before it does. Uninstalling leaves each user's `%LOCALAPPDATA%\AdminConsole`, which is theirs, not the program's.

The release is **not code-signed** yet, so SmartScreen will warn on first run. Check the file against `SHA256SUMS.txt`
on the release page.

The package carries no app registration. The first run opens a setup screen that asks for the client ID of the
registration made in your tenant (next section).

**Requirements:** Windows 10 2004 or later, or Windows 11; an Entra ID tenant and an account with the directory roles
for whatever you intend to do. Exchange, OneDrive and AD features light up when the tenant has them; where it does not,
they say so instead of failing.

## Register the app

The console signs in through an app registration **in the tenant it manages** — one per tenant, created by a script,
owned by that tenant rather than by us:

```powershell
.\scripts\New-AppRegistration.ps1 -WhatIf   # signs in, resolves every permission, creates nothing
.\scripts\New-AppRegistration.ps1           # does it, and prints the client ID
.\scripts\New-AppRegistration.ps1 -Update   # an existing registration: adds what newer versions need
```

It creates a public client with no secret, adds the redirect URIs the Windows broker needs, requests the delegated
permissions — 26 on Microsoft Graph, `Exchange.Manage` on Exchange Online and `AllSites.FullControl` on SharePoint —
grants admin consent, and prints the client ID to paste into the setup screen, or pass `-WriteConfig` and there is
nothing to paste. Undoing it is the single `Remove-MgApplication` command it prints when it finishes.

Per-tenant rather than one shared multi-tenant app is deliberate: the app object belongs to the client, so it survives
their security review, and nothing we own holds standing access to their directory. The full detail, the permission
list by tier, the roles an admin needs, and the manual fallback are in
[docs/08-APP-REGISTRATION.md](docs/08-APP-REGISTRATION.md).

Read permissions are requested at sign-in. Write permissions are requested the first time you change something;
the sensitive ones only when you click a reveal button; the PIM ones when you open My roles; file access at the first
OneDrive export.

**Another tenant.** Title bar → tenant name → *Add a tenant…*: the tenant's ID or domain, then the client ID of the
registration made there. Each tenant reopens with the account last used for it.

## On-prem Active Directory

Off until a domain is named. On a domain-joined machine, the domain alone is enough: Windows finds a controller and
the bind is your own Kerberos ticket.

```json
{ "OnPrem": { "Domain": "corp.example.com" } }
```

That goes in `appsettings.local.json` beside `AdminConsole.exe`, or per tenant in `tenant.json` as the profile's
`domains` list (several domains are fine; writes go to the domain that holds the object). `Server` and a credential
exist for labs and troubleshooting, never for a client deployment — see [docs/11-ONPREM-AD.md](docs/11-ONPREM-AD.md).

## Where it keeps things

Everything is under `%LOCALAPPDATA%\AdminConsole`:

| Path | What |
|---|---|
| `tenant.json` | The tenants this installation opens: registration, tenant, last account, on-prem domains. Never a password. |
| `cache\{tenant}.db` | The local index, per tenant. Delete it or use Settings → Rebuild index to start again. |
| `actions.db` | The action log: every write, with its tenant and outcome. |
| `templates\users\{tenant}\*.json` | User templates, per tenant. Hand-editable. |
| `templates\offboarding\*.json` | Offboarding profiles. Hand-editable. |
| `snapshots\{tenant}\` | What offboarding saved before it changed anything, for restore. |
| `pending\{tenant}.json` | Hybrid creates waiting for Entra Connect. |
| `reports\{tenant}\` | The last build of each report, with its date. |
| `settings.json` | Sync intervals, theme, density. |
| `branding.json` | Product name, tagline and colours. Absent means the neutral default. |
| `logs\*.log` | Application logs. Object IDs and error text, never tokens or passwords. |
| `msal.cache` | The token cache, protected by DPAPI. |

![Offboarding preview](docs/images/offboard-preview.png)

## Branding it

The console ships branded to nobody: it calls itself **Admin Console** and paints itself slate and blue. One file
changes that, so a deployment can carry whoever's identity it should — a client's, a consultancy's, or an internal IT
team's.

Either fill it in from **Settings → Branding** in the app, or drop the file in yourself:

`%LOCALAPPDATA%\AdminConsole\branding.json`

```json
{
  "productName": "Contoso IT Console",
  "tagline": "Contoso identity and devices, in one place.",
  "chromeColor": "#2B1B4D",
  "accentColor": "#C08CFF"
}
```

| Field | Where it shows | If you leave it out |
|---|---|---|
| `productName` | Title bar, sign-in page, Settings | `Admin Console` |
| `tagline` | One line under the name on the sign-in page | The default line |
| `chromeColor` | Title bar, navigation rail, status bar, side panels | `#1F2733` |
| `accentColor` | Primary buttons, the active nav item, focus rings, the figures on Home | `#58A6FF` |

Colours are applied when the app starts, so **restart to see a change**. Everything derived from those two — the
rail's shade, and whether text on the chrome comes out light or dark — is computed rather than configured, so a pale
chrome gets dark text instead of becoming unreadable. A value that is not a hex colour is ignored in favour of the
default; a typo cannot leave you with a window you can't read your way out of.

**Deploying it branded.** `branding.json` sits beside the cache, not in the install directory, so ship it with the
profile or drop it in during setup and nobody has to open Settings at all. The MSI itself can carry the name too:
`scripts\Build-Release.ps1 -ProductName "Contoso IT Console" -Manufacturer "Contoso"`. If you are also creating the app
registration, pass a matching `-Name` so it is recognisable in the tenant's app list:

```powershell
.\scripts\New-AppRegistration.ps1 -Name "Contoso IT Console" -WriteConfig
```

**The icon** is neutral too — the default slate and blue, a magnifier over a person. A branded build carries its own:
`scripts\Build-Release.ps1 -IconPath .\contoso.ico` puts it on the exe, the window, the Start menu and Apps & features.
The source of the default is `scripts/icon/New-AppIcon.py`.

**What is not brandable at run time.** There is no logo slot and no font override. Two colours and a name cover what a console
needs to feel like the organisation's own; more surface than that is more ways to make it unreadable, and the contrast
work recorded in `docs/ACCESSIBILITY.md` only holds because the palette is small.

## Keyboard

| Key | Does |
|---|---|
| `Ctrl+K` | Command palette. `u:` `d:` `g:` narrow it, `>` runs an action |
| `Ctrl+N` | New user from a template |
| `Ctrl+Shift+O` | Offboard the user on screen |
| `Ctrl+T` / `Ctrl+W` | New tab, close tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next tab, previous tab |
| `Alt+1`…`Alt+8` | Home, Users, Groups, Devices, Templates, Reports, Activity, Domain |
| `Esc` | Close the palette or a dialog |
| `Alt+←` | Back |
| `F5` | Re-read the object on screen |
| `Ctrl+Shift+S` | Sync now |
| `Ctrl+,` | Settings |

![Activity log](docs/images/activity-log.png)

## Build, test, release

```powershell
dotnet restore
dotnet build AdminConsole.sln -c Debug
dotnet test tests/AdminConsole.Core.Tests
dotnet run --project src/AdminConsole.App
pwsh scripts/Build-Release.ps1               # MSI + zip for x64 and arm64, and SHA256SUMS, in artifacts\<version>
```

Building needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0); the MSI needs nothing else (WiX
5.0.2 comes from NuGet). The release script blanks the client ID in the shipped settings and refuses to package if
anything local — `appsettings.local.json`, or any JSON holding a credential — is in the publish folder.

## Repository

```
src/AdminConsole.Core/    no WPF, fully testable — Auth, Graph, Cache, Sync, Search, Users, Groups, Devices,
                          Licensing, Exchange, SharePoint, OneDrive, OnPrem, Offboarding, Templates, Reports, Pim
src/AdminConsole.App/     the WPF shell, view-models and views
tests/AdminConsole.Core.Tests/
installer/                the WiX MSI
scripts/                  the app registration, the release build, and the icon's source
docs/                     the specification this was built from
```

Core never references App, and App never calls Graph, Exchange or SharePoint directly. All three are reached over
`HttpClient` rather than SDKs or hosted PowerShell, so batching, delta queries, throttling and payload size stay under
our control — see [docs/04-GRAPH-REFERENCE.md](docs/04-GRAPH-REFERENCE.md).

## Documentation

| File | Read when |
|---|---|
| [docs/00-README.md](docs/00-README.md) | Finding your way around the rest |
| [docs/09-PLAN.md](docs/09-PLAN.md) | Seeing what is built and what each milestone verified |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Wondering why something was done that way |
| [docs/08-APP-REGISTRATION.md](docs/08-APP-REGISTRATION.md) | Setting up a tenant, or adding one |
| [docs/11-ONPREM-AD.md](docs/11-ONPREM-AD.md) | Turning on Active Directory |
| [docs/13-EXCHANGE-ONLINE.md](docs/13-EXCHANGE-ONLINE.md) | How mailboxes are read and changed |
| [docs/14-ONEDRIVE.md](docs/14-ONEDRIVE.md) | OneDrive settings and exports |
| [docs/06-OFFBOARDING-CATALOG.md](docs/06-OFFBOARDING-CATALOG.md) | What each offboarding step does, and how restore undoes it |
| [docs/PERF.md](docs/PERF.md) | Checking the measured numbers against the targets |
| [docs/ACCESSIBILITY.md](docs/ACCESSIBILITY.md) | Checking the keyboard, naming and contrast pass |
| [docs/BACKLOG.md](docs/BACKLOG.md) | What is known to be rough |

## Status

**v0.3.2** — Phases 1 to 3 are built: the cloud console, on-prem AD, and Exchange Online, OneDrive, hybrid onboarding,
reports and several tenants. Each milestone was verified against a live tenant and a lab domain; every gate and what it
actually proved is in [docs/09-PLAN.md](docs/09-PLAN.md), including the places where a gate could not be met in full
and why. Three gates are the owner's to run, because they cannot be simulated: a workday of real helpdesk tasks, a
stranger using it cold, and two production tenants side by side. What is known to be rough is in
[BACKLOG.md](docs/BACKLOG.md) rather than left for you to discover.
