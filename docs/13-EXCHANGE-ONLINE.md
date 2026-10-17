# 13 — Exchange Online (Phase 3)

Status: **route proven, nothing built.** Every claim in §1–§4 marked *proven* was observed against the YeomanLabs
tenant on 2026-10-17 through a throwaway harness that used this console's own `AuthService` and registration. Nothing
else in this document ships yet.

Phase 1 greys the Mailbox tab and five offboarding steps with "Requires Exchange Online integration (Phase 3)". This is
the design for taking that wall down, the way doc 11 was for AD.

---

## 1. Which endpoint

Graph cannot do this. Delegation, forwarding, automatic replies, holds, hiding from address lists and conversion to
shared all belong to Exchange, and Graph's delegated `MailboxSettings.*` scopes only reach mailboxes the signed-in admin
can already open. There are two Exchange REST surfaces:

| | `adminapi/v2.0` — the Exchange Online Admin API | `adminapi/beta/{tenant}/InvokeCommand` |
|---|---|---|
| Status | Documented on Microsoft Learn; preview, with a GA in 2026 | Undocumented; it is what the ExchangeOnlineManagement v3 module itself calls |
| Coverage | Six endpoints. Writes only Send on Behalf (`Set-Mailbox -GrantSendOnBehalfTo`) and folder permissions | Effectively every cmdlet the signed-in admin's Exchange roles allow |
| Permission | `Exchange.ManageV2`, renamed `Exchange.AdminAPI.Manage` at GA | `Exchange.Manage` |

**Decision: InvokeCommand.** v2.0 cannot convert a mailbox, hide it, forward it, hold it, or grant Full Access or Send
As, which is most of this phase. The risk of an undocumented route is real and is contained the same way Graph's beta
calls are: every call goes through one client, and a cmdlet that v2.0 covers can move there individually when v2.0
grows. If InvokeCommand stops working outright, the fallback is the documented module run **out of process** (`pwsh`
with ExchangeOnlineManagement, delegated, as the admin), never hosted in-process — CLAUDE.md non-negotiable 5.

### The request (proven)

```
POST https://outlook.office365.com/adminapi/beta/{tenantId}/InvokeCommand?$select=A,B,C
Authorization: Bearer <token, aud https://outlook.office365.com>
Content-Type: application/json
X-AnchorMailbox: UPN:<signed-in admin's UPN>
X-ResponseFormat: json
Prefer: odata.maxpagesize=1000

{ "CmdletInput": { "CmdletName": "Get-Mailbox", "Parameters": { "Identity": "<entra object id>" } } }
```

- **Token:** scope `https://outlook.office365.com/Exchange.Manage`, delegated. On the YeomanLabs tenant, Exchange's
  service principal lists it as `Exchange.Manage` (id `ab4f2b77-0b06-4fc1-a9de-02113fc2ab7c`), type **User**, which means
  a user can consent to it and admin consent is not required. The registration lists it and holds an `AllPrincipals`
  grant so nobody sees a consent prompt (M13.3). Consent alone does nothing: Exchange RBAC decides what runs, so the
  admin needs Exchange Administrator, Exchange Recipient Administrator or Global Administrator.
- **`X-AnchorMailbox`:** the *target* mailbox for any call about one mailbox — `OID:{object id}@{tenant id}`, proven
  on this endpoint — and the admin's own (`UPN:{admin}`) only for tenant-wide cmdlets. **Corrected by the M13 gate.**
  This section first said the admin's UPN was enough for every call, because every probe had targeted the admin's own
  mailbox. Opened on anyone else's, `Get-MailboxRegionalConfiguration` answers 400 `CmdletProxyNotAvailableException`
  "Cmdlet needs proxy. Current Server FQDN: … Required Server FQDN: …", because it must run on the server holding that
  mailbox. Statistics, permissions, auto-replies and folder permissions proxied themselves and hid the problem.
- **`$select`** is honoured and an unknown name is skipped, not rejected. `Get-Mailbox` without it returns 338 properties.
- **Identity is the Entra object id** (`ExternalDirectoryObjectId`). Proven for `Get-Mailbox`, `Get-MailboxStatistics`,
  `Get-MailboxFolderStatistics`, `Get-MailboxFolderPermission` (`{oid}:\Calendar`) and `Get-Recipient`. Never use the
  `Identity` Exchange hands back (`AndrewYeoman`): that is the object's Name, it changes on rename, and it is the same
  trap doc 11 avoided by addressing AD objects by `objectGUID` rather than DN.

### The response (proven)

```
{ "@odata.context": "…/$metadata#Collection(Exchange.GenericHashTable)",
  "adminapi.warnings@odata.type": "#Collection(String)", "@adminapi.warnings": [],
  "value": [ { "PrimarySmtpAddress": "…", "EmailAddresses@odata.type": "#Collection(String)", "EmailAddresses": [ … ] } ],
  "@odata.nextLink": "…" }
```

- Always a `value` array, even for one object. Each property may carry an `@odata.type` sibling; ignore those.
- **Sizes are strings.** `"82.63 MB (86,646,374 bytes)"`, `"100 GB (107,374,182,400 bytes)"`, or `"Unlimited"`. Parse
  the byte count in the brackets; never the rounded figure in front of it.
- **Full Access rights arrive joined.** `Get-MailboxPermission` returns `"AccessRights": ["FullAccess, ReadPermission"]`,
  a one-element array holding a comma-separated string. Split it.
- `NT AUTHORITY\SELF` appears in both permission lists. It is the mailbox's own access, so filter it out.
- A `Set-*` that succeeds returns an empty body.
- `@adminapi.warnings` carries what PowerShell would print as a warning. A write can succeed and still warn, which is
  the same as doc 11's `DirectoryOutcome.Warning`, and the console must show it rather than drop it.

### Paging (proven)

`Parameters.ResultSize = N` returns N rows and an `@odata.nextLink`. POST the same body to that link for the next
page. A 3-row page walked 11 mailboxes in four calls. The link expires in 5–10 minutes, so a walk runs to the end and
never resumes. `ResultSize = "Unlimited"` still pages at the `Prefer` size.

Every page that carries a nextLink also carries the warning *"There are more results available than are currently
displayed. To view them, increase the value for the ResultSize parameter."* That is PowerShell advice to a person, and
it stops being true once the link is followed, so the client drops it from pages it follows and keeps it anywhere else,
where it would mean real truncation.

### Errors (proven)

```
404 { "error": { "code": "NotFound", "message": "Error executing cmdlet",
  "details": [ { "code": "Context", "message": "Ex6F9304|Microsoft.Exchange.Configuration.Tasks.ManagementObjectNotFoundException|The operation couldn't be performed because object '…' couldn't be found on '…'." } ],
  "innererror": { … } } }
400 { "error": { "code": "BadRequest", "message": "Invalid Operation",
  "details": [ { "code": "Client", "message": "|Microsoft.Exchange.AdminApi.CommandInvocation.AmbiguousParameterSetException|Parameter set cannot be resolved…" } ] } }
```

`error.message` is useless ("Error executing cmdlet"). The real content is `details[0].message`: a pipe-delimited
`{trace id}|{.NET exception type}|{text}`. `ExchangeException` keeps all three. The friendly-text map keys on the
**exception type**, the only stable identifier, the way `DirectoryErrorText` keys on the Win32 sub-code rather than the
LDAP result code. The server name in the text (`MW4PR19A17DC006…`) is noise for an admin, so it is cut.

### Throttling (proven headers, unprovoked 429)

Every response carries `Rate-Limit-Limit: 3000`, `Rate-Limit-Remaining`, `Rate-Limit-Reset` (an ISO timestamp), and
`X-SLT-ConcurrencyLimit`. A 429 has not been provoked. The client honours `Retry-After` when present, otherwise waits
until `Rate-Limit-Reset`, otherwise backs off 1-2-4-8 s. Concurrency is 3 per client, below Graph's 4 because one
Exchange call costs far more than one Graph call. Observed latency is 200–2,000 ms per call, and 3.4 s for the first
call after a cold start.

---

## 2. What the console reads, and from where

| Need | Cmdlet | Notes |
|---|---|---|
| Mailbox type for every user | `Get-User -ResultSize Unlimited` `$select=ExternalDirectoryObjectId,RecipientTypeDetails` | One paged walk covers users **with no mailbox** too (`User`), which `Get-Mailbox` cannot say |
| Primary SMTP, hidden, synced | `Get-Mailbox -ResultSize Unlimited` `$select=ExternalDirectoryObjectId,PrimarySmtpAddress,RecipientTypeDetails,HiddenFromAddressListsEnabled,IsDirSynced` | Cached. There is no delta, so it refreshes on the managed-devices interval |
| Mailbox properties | `Get-Mailbox -Identity {oid}` with a `$select` | Addresses, forwarding, Send on Behalf, archive, hold, quotas, restrictions |
| Size | `Get-MailboxStatistics -Identity {oid}` | `-Archive` for the archive when `ArchiveStatus` is `Active` |
| Full Access | `Get-MailboxPermission -Identity {oid}` | Filter SELF and inherited `Deny` rows |
| Send As | `Get-RecipientPermission -Identity {oid}` | Filter SELF |
| Folder permissions | `Get-MailboxFolderStatistics -Identity {oid} -FolderScope X` → `Get-MailboxFolderPermission -Identity {oid}:\{Name}` | **Folder names are localised.** `FolderType` (`Calendar`, `Inbox`, `Contacts`, `SentItems`…) is not, so resolve by type and use the name it returns. Never hardcode `\Calendar` |
| Regional | `Get-MailboxRegionalConfiguration` | Language, time zone, date and time format, localised folder names |
| Automatic replies | `Get-MailboxAutoReplyConfiguration` | Message bodies are mailbox content and are never logged |
| Protocols | `Get-CASMailbox` | OWA, ActiveSync, POP, IMAP, MAPI, EWS |
| Inbox rules | `Get-InboxRule -Mailbox {oid}` | Offboarding: a leaver's forwarding rules |
| Mobile devices | `Get-MobileDevice -Mailbox {oid}` | Offboarding |
| Accepted domains | `Get-AcceptedDomain` | The address editor's domain list |

All of these were called successfully on 2026-10-17.

The Mailbox tab does not wait for all of them. Sections load independently in parallel (three at a time, per the
concurrency limit) and each one says what it is waiting for, as User 360 does with its Graph batch. Folder permissions
are fetched only when that section is opened, because they take two calls per folder.

---

## 3. Writes (M14)

Every write goes through `ExchangeService`, logs to the action log with an inverse payload where one exists, and
reads the object back afterwards rather than trusting the empty 200, the same write-through rule as Graph.

| Change | Cmdlet | Reversible |
|---|---|---|
| Addresses | `Set-Mailbox -EmailAddresses @{add=…; remove=…}` (`#Exchange.GenericHashTable`) | ✅ |
| Hide from address lists | `Set-Mailbox -HiddenFromAddressListsEnabled` | ✅ |
| Convert regular ↔ shared | `Set-Mailbox -Type Shared/Regular` | ✅ |
| Forwarding | `Set-Mailbox -ForwardingAddress -DeliverToMailboxAndForward` | ✅ |
| Send on Behalf | `Set-Mailbox -GrantSendOnBehalfTo @{add/remove}` | ✅ |
| Full Access | `Add-/Remove-MailboxPermission -AccessRights FullAccess -AutoMapping` | ✅ |
| Send As | `Add-/Remove-RecipientPermission -AccessRights SendAs` | ✅ |
| Sent Items copies | `Set-Mailbox -MessageCopyForSentAsEnabled/-MessageCopyForSendOnBehalfEnabled` | ✅ |
| Automatic replies | `Set-MailboxAutoReplyConfiguration` | ✅ |
| Regional | `Set-MailboxRegionalConfiguration -LocalizeDefaultFolderName` | ✅ (folder renames are not) |
| Archive | `Enable-Mailbox -Archive`; `-AutoExpandingArchive` | Archive disable is destructive after 30 days, so it is offered as a confirmed action, never as an undo |
| Litigation hold | `Set-Mailbox -LitigationHoldEnabled -LitigationHoldDuration` | ✅ |
| Folder permissions | `Add-/Set-/Remove-MailboxFolderPermission` | ✅ |
| Delivery restrictions | `Set-Mailbox -AcceptMessagesOnlyFrom…/-RejectMessagesFrom…` | ✅ |

### The hybrid rule

A mailbox whose user is synced from AD (`IsDirSynced`) has some Exchange attributes **mastered on-prem** when the
organisation keeps an Exchange server or the Exchange schema in AD. Exchange Online refuses those writes for a synced
object, and the change has to be made in AD and synced up:

| Attribute | AD attribute | Written by |
|---|---|---|
| Email addresses | `proxyAddresses` | AD, through `IDirectoryService`, when a DC is reachable and the schema has it |
| Hidden from address lists | `msExchHideFromAddressLists` | AD, same |
| Send on Behalf | `publicDelegates` | AD, same |
| Forwarding to a recipient | `altRecipient` + `deliverAndRedirect` | AD, same |
| Mailbox type (shared) | `msExchRemoteRecipientType` / `msExchRecipientTypeDetails` | **Not offered.** Done with `Set-RemoteMailbox -Type Shared` on an Exchange server, which the console does not have |

Everything else — Full Access, Send As, folder permissions, automatic replies, regional settings, holds, archive — is
written in Exchange Online for synced and cloud mailboxes alike.

M12.4 already answers whether an attribute exists in the forest's schema and whether this admin may write it
(`allowedAttributesEffective`). A forest without the Exchange extension (the lab has none, since `mailNickname` is
absent) greys these four with that reason rather than attempting them. **This branch cannot be proven here**: no
YeomanLabs mailbox is synced (the two lab-synced users are `User`, with no mailbox), and the lab forest has no Exchange
schema. It is built against the rule, unit-tested, and said to be unproven, in the same terms M12.4 used.

---

## 4. Shape in code

```
Core/Exchange/
  ExchangeClient          InvokeCommand over HttpClient: token, headers, $select, paging, retry, concurrency 3
  ExchangeException       status, error.code, exception type, text, trace id, request-id, raw body
  ExchangeErrorText       exception type → plain English, as GraphErrorText and DirectoryErrorText do
  ExchangeValues          the parsers for the string encodings: sizes, joined rights, Unlimited, dates
  ExchangeService         the operations in the console's vocabulary, addressed by Entra object id
  MailboxDetail / MailboxPermissionEntry / FolderPermissionEntry / …  projections
```

`ExchangeClient` is not `GraphClient` with a different URL: the body, the error shape, the paging and the throttling
signals are all different, and sharing a class would make each of them a branch. What they do share — the retry loop's
shape and `IAuthService` — is small.

`IAuthService.GetTokenAsync` already accepts any scope list, so the Exchange token is a second call with the
Exchange scope. MSAL caches each audience separately. Granted-scope checks for Exchange read the Exchange token's
`scp`, not the Graph token's, which knows nothing about another resource.

---

## 5. Things that will bite

- **The Graph `scp` cannot tell you about Exchange.** A Graph token never lists `Exchange.Manage`. The grey rule must
  ask for an Exchange token, and "no Exchange token" must read as "not granted", not as "error".
- **Consent is not permission.** A token with `Exchange.Manage` and an admin with no Exchange role gets a working
  `Get-Mailbox` for their own mailbox and 403s elsewhere. The message names the role, not the scope.
- **Propagation.** A fresh grant took about a minute before the broker would return the token silently. During that
  minute the failure looked exactly like a broken configuration.
- **WAM needs a real window.** A probe with no console window got `window_handle_required`, and one borrowing another
  process's foreground window got `IncorrectConfiguration`, which blames the redirect URI even though it is fine. The
  app always has its own window, so this bites only harnesses.
- **Folder names are localised**, and a mailbox whose owner changes language changes them (or not, depending on
  `LocalizeDefaultFolderName`). Resolve by `FolderType` every time.
- **`Set-Mailbox -Type Shared` does not remove a licence**, and removing the licence first deletes the mailbox after the
  30-day grace period. Offboarding orders convert-to-shared before licence removal (doc 06), and the preview says why.
- **Litigation hold needs a licence that includes it** (Exchange Online Plan 2, or the archiving add-on). A shared
  mailbox under 50 GB without a licence cannot be held. Say so before the click.

---

## 6. What this does not do

- Create or delete mailboxes, other than through licensing.
- Run anything app-only, or against any tenant but the signed-in admin's.
- Message trace, transport rules, connectors, or anything organisation-wide. This is a console for people.
- PST export. That is a Purview eDiscovery job, not an Exchange cmdlet, and it belongs in the portal.
