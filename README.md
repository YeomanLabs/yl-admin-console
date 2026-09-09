# YeomanLabs Admin Console

A Windows desktop console that puts Entra ID and Intune behind one search box.

Open it, type part of a name, and you are on the person or the machine. Everything about them is on one screen: who
they are, what they can sign in with, what they are licensed for, which groups they are in, and which devices are
theirs. The things a helpdesk does all day — reset a password, remove a stale MFA method, fix a licence, offboard a
leaver, create a starter from a template — are one click from there instead of four browser tabs.

Phase 1 is cloud-only: Entra users, groups and devices, Intune devices, and the actions above. On-prem Active Directory
is Phase 2. Exchange Online is Phase 3. Neither is stubbed out and pretending; where a step needs them it is listed and
greyed with the reason.

![Home](docs/images/home.png)

## What it does

- **Search that answers instantly.** Everything is indexed locally, so the palette never waits on Graph. At 5,000 users,
  2,000 devices and 1,000 groups it answers in a median of 1.1 ms — see [PERF.md](docs/PERF.md).
- **Lists you can actually browse.** Users, groups and devices, sortable and filterable from the same local cache, with
  filters phrased as the questions a helpdesk asks: who is disabled, who has no licence, which devices are failing
  compliance, which are registered but never enrolled, which groups nobody is in. Every count is clickable.
- **One page per person.** Identity, access, devices and activity, editable in place, with anything mastered on-prem
  greyed and labelled rather than silently failing.
- **Actions that explain themselves.** Every destructive action previews exactly what it will change, names the real
  objects, and says why a step cannot run. Reversible changes offer an undo.
- **Offboarding as a checklist.** Nineteen catalogued steps, previewed in parallel and run in order, with a live log and
  an undo for the reversible half.
- **Users from templates.** Snapshot a role from someone who already has it, then create a starter with the naming rules
  filled in and the sign-in name checked while you type.
- **A written record.** Every write lands in a local action log with what was sent, what came back, and whether it was
  undone. The Activity page filters it and copies it as markdown.

![The Users list](docs/images/users.png)

Home is a landing page, not a splash screen: every figure on it opens the list already filtered to it, so "2 enabled
but unlicensed" is one click from the two people it means.

![User 360](docs/images/user-360.png)

## What it deliberately does not do

No telemetry. No licence server. No crash reporting. The only outbound calls are to `login.microsoftonline.com` and
`graph.microsoft.com`; nothing about your tenant leaves your machine.

No service accounts and no application permissions. It signs in as you, through the Windows broker, with delegated
scopes only, and asks for write permission the first time you change something rather than up front.

## Requirements

- Windows 10 2004 or later, or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build; the .NET 8 Desktop Runtime to run
- An Entra ID tenant, and an account with the directory roles for whatever you intend to do

## Build and run

```powershell
dotnet restore
dotnet build YeomanLabs.AdminConsole.sln -c Debug
dotnet test tests/YeomanLabs.AdminConsole.Core.Tests
dotnet run --project src/YeomanLabs.AdminConsole.App
```

The Core project is plain .NET and testable anywhere. The App project is WPF and needs Windows.

## Register the app

The console signs in through an app registration **in the tenant it manages** — one per tenant, created by a script,
owned by that tenant rather than by us:

```powershell
.\scripts\New-AppRegistration.ps1 -WhatIf   # signs in, resolves every permission, creates nothing
.\scripts\New-AppRegistration.ps1           # does it, and prints the client ID
```

It creates a public client with no secret, adds the redirect URIs the Windows broker needs, requests the 22 delegated
permissions, grants admin consent, and prints the client ID to paste into the console's setup screen — or pass
`-WriteConfig` and there is nothing to paste. Undoing it is the single `Remove-MgApplication` command it prints when
it finishes.

Per-tenant rather than one shared multi-tenant app is deliberate: the app object belongs to the client, so it survives
their security review, and nothing we own holds standing access to their directory. The full detail, the permission
list, the roles an admin needs, and the manual fallback are in
[docs/08-APP-REGISTRATION.md](docs/08-APP-REGISTRATION.md).

Read permissions are requested at sign-in. Write permissions are requested the first time you change something, and the
two sensitive ones only when you click a reveal button.

## Where it keeps things

Everything is under `%LOCALAPPDATA%\YeomanLabs\AdminConsole`:

| Path | What |
|---|---|
| `cache\{tenant}.db` | The local index. Delete it or use Settings → Rebuild index to start again. |
| `actions.db` | The action log: every write, with its outcome. |
| `templates\users\*.json` | User templates. Hand-editable. |
| `templates\offboarding\*.json` | Offboarding profiles. Hand-editable. |
| `settings.json` | Sync intervals, theme, density. |
| `tenant.json` | Which app registration this installation signs in with. |
| `logs\*.log` | Application logs. Object IDs and error text, never tokens or passwords. |
| `msal.cache` | The token cache, protected by DPAPI. |

![Offboarding preview](docs/images/offboard-preview.png)

## Keyboard

| Key | Does |
|---|---|
| `Ctrl+K` | Command palette. `u:` `d:` `g:` narrow it, `>` runs an action |
| `Ctrl+N` | New user from a template |
| `Ctrl+Shift+O` | Offboard the user on screen |
| `Ctrl+T` / `Ctrl+W` | New tab, close tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next tab, previous tab |
| `Alt+1`…`Alt+6` | Home, Users, Groups, Devices, Templates, Activity |
| `Esc` | Close the palette or a dialog |
| `Alt+←` | Back |
| `F5` | Re-read the object on screen |
| `Ctrl+Shift+S` | Sync now |
| `Ctrl+,` | Settings |

![Activity log](docs/images/activity-log.png)

## Repository

```
src/YeomanLabs.AdminConsole.Core/    no WPF, fully testable — Auth, Graph, Cache, Sync, Search,
                                     Users, Groups, Devices, Licensing, Offboarding, Templates
src/YeomanLabs.AdminConsole.App/     the WPF shell, view-models and views
tests/YeomanLabs.AdminConsole.Core.Tests/
docs/                                the specification this was built from
```

Core never references App, and App never calls Graph directly. Graph is reached over `HttpClient` rather than the
Microsoft Graph SDK, so batching, delta queries, throttling and payload size stay under our control — see
[docs/04-GRAPH-REFERENCE.md](docs/04-GRAPH-REFERENCE.md).

## Documentation

| File | Read when |
|---|---|
| [docs/00-README.md](docs/00-README.md) | Finding your way around the rest |
| [docs/09-PLAN.md](docs/09-PLAN.md) | Seeing what is built and what each milestone verified |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Wondering why something was done that way |
| [docs/PERF.md](docs/PERF.md) | Checking the measured numbers against the targets |
| [docs/ACCESSIBILITY.md](docs/ACCESSIBILITY.md) | Checking the keyboard, naming and contrast pass |
| [docs/BACKLOG.md](docs/BACKLOG.md) | What is known to be rough |

## Status

Phase 1 is complete and being dogfooded, which is where most of the recent work has come from: the list pages, the
landing page, a contrast pass and several bugs all came out of real use rather than out of the plan. What is known to
be rough is in [BACKLOG.md](docs/BACKLOG.md) rather than left for you to discover.

Phase 1 is complete and being dogfooded. It has been built and verified against a live tenant milestone by milestone;
every gate and what it actually proved is recorded in [docs/09-PLAN.md](docs/09-PLAN.md), including the places where a
gate could not be met in full and why.
