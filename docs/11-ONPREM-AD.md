# 11 — On-prem Active Directory (Phase 2)

Status: **design, not built.** Nothing in this document ships yet. It exists so the shape can be argued with before
code is written, and so a client's AD team can review what the console would do to their directory.

Phase 1 is cloud-only and treats on-prem as a wall: 20 user attributes are read-only with *"Mastered in on-prem AD
(Phase 2)"*, and two offboarding steps are registered but greyed. Phase 2 is mostly about taking that wall down.

---

## 1. How it connects

**LDAP straight to a domain controller, as the signed-in Windows user.**

The console already signs in to Entra as the admin rather than as a service principal (CLAUDE.md non-negotiable 4).
The on-prem equivalent is the admin's own Kerberos ticket: the console binds to a DC with Negotiate, and AD enforces
whatever that person has been delegated. No agent, no service account, no stored credential.

```
Admin workstation (domain-joined)
  └─ Admin Console
       ├─ HTTPS ──────────────► graph.microsoft.com        (Phase 1, delegated, MSAL)
       └─ LDAP/LDAPS ─────────► dc.corp.example.com:389/636 (Phase 2, Kerberos, integrated)
```

### Rejected alternatives

| Option | Why not |
|---|---|
| A gateway or sync service | Needs a service account with standing rights over the directory, plus something to host, secure and patch. Breaks non-negotiable 4 and is the thing a security review will object to |
| Shelling out to the RSAT `ActiveDirectory` module | A heavy client-side prerequisite, slow per call, and it turns typed LDAP errors into parsed text |
| Writing through Graph and letting Entra Connect push down | Does not exist. Entra Connect is one-way for these attributes; only password writeback goes the other way |

### What this requires, and what happens without it

- The workstation must reach a DC — corporate network or VPN. Off-network, Phase 2 features stay greyed with
  *"No domain controller reachable"*, exactly as Phase 1 greys the Exchange steps.
- The workstation should be domain-joined for integrated auth. Explicit credentials are supported for the lab and
  for cross-forest cases, but are not the intended production path.
- Discovery is by DNS SRV (`_ldap._tcp.dc._msdcs.<domain>`), not a configured hostname. A hardcoded DC is a single
  point of failure and goes stale.

---

## 2. Joining an Entra object to its AD object

The console **already reads and caches** everything needed for this — `GraphSelects` pulls `onPremisesImmutableId`,
`onPremisesDistinguishedName` and `onPremisesSamAccountName` today. So no search is needed to find the AD object of a
synced user; the anchor is already on screen.

| Direction | Key |
|---|---|
| Entra user → AD user | `onPremisesImmutableId` is base64 of `ms-DS-ConsistencyGuid`, falling back to `objectGUID` (doc 05 §150) |
| Bind | `Convert.FromBase64String(immutableId)` → `Guid` → `LDAP://<GUID=…>` — exact, and survives a rename or an OU move |
| Fallbacks | `onPremisesDistinguishedName` (breaks on move), `onPremisesSamAccountName` (breaks on rename), `onPremisesSecurityIdentifier` |

`onPremisesDistinguishedName` is the one to show and never to key on. It is the value an admin recognises, and the
value that is wrong the moment somebody moves the object.

---

## 3. What becomes possible

### Users

Most of this is unlocking `DirectoryUser.OnPremMasteredProperties` — the 20 attributes currently greyed. With a DC
reachable, the same field writes to AD instead of Graph, and the caption changes from a refusal to a sync note.

Create (into a chosen OU), edit those attributes, enable/disable, unlock, set a password, move between OUs, rename,
delete. Group membership. Manager, resolved to a real object rather than a name.

### Groups

The part with no cloud equivalent: **scope** (Domain Local / Global / Universal) and **category** (Security /
Distribution). Both are `groupType` bit flags, both constrain what can be a member of what, and neither exists in
Entra. Create, edit, membership including nested groups, move, rename, delete.

### Computers

AD computer objects are **not** the same as Entra devices, and conflating them would be the easiest way to mislead
somebody. A machine can be in AD only, Entra only, both (hybrid-joined), or in Intune but neither. The device page
would need to say which, the way the Devices list already distinguishes "Not in Intune".

Find, enable/disable, move OU, reset the computer account, read Windows LAPS from AD (`msLAPS-Password`, or the
legacy `ms-Mcs-AdmPwd`), delete.

### Organisational units

Browse as a tree, create, rename, move, delete. Used directly by "create user in…" and by the offboarding
*"Move the AD object to the disabled OU"* step that is already registered and waiting.

---

## 4. The two things that are genuinely hard

### 4.1 The permission model does not match

Phase 1's "explain the grey" rests on something AD does not have. Graph tells you your scopes up front, so
`PermissionChecker` can decide before showing a button. AD has ACLs and delegation — an admin may hold rights on one
OU and not its sibling, and the reliable way to know is to attempt the write.

So Phase 2's greying is necessarily weaker:

- Pre-flight where it is cheap and certain — the object is protected from accidental deletion, the group is
  `CN=Domain Users` (primary group, cannot be removed), the attribute is system-owned.
- Otherwise, attempt and translate. LDAP result codes map to plain English much as `GraphErrorText` does:
  `insufficientAccessRights` (50) → *"Your account has not been delegated permission to do this on that OU"*,
  `unwillingToPerform` (53) with `0000052D` → *"The password does not meet the domain's policy"*, and so on.
- **This must be stated in the UI**, not silently different. A button that might fail is not the same promise as a
  button that will work, and the console's whole posture is not making the admin guess.

### 4.2 Sync latency becomes visible

Entra Connect runs every 30 minutes by default. Write to AD and the cloud view is stale until it runs.

`docs/01-PRODUCT.md:19` already committed to this: *"Sync state is always visible. Hybrid admins should never wonder
'did that sync yet.'"* Concretely — read back from AD immediately (authoritative, instant), mark the Entra copy as
pending with the time of the last sync from `onPremisesLastSyncDateTime`, and never show a stale cloud value next to
a fresh on-prem one without saying which is which.

A related trap already handled in Phase 1 and worth keeping: `accountEnabled` is *accepted* by Graph on a synced user
and then reverts on the next sync cycle. `DirectoryUser.SyncedDisableWarning` says so. In Phase 2 the same action
should simply write to AD instead.

---

## 5. Things that will bite

- **Setting a password requires an encrypted connection.** `unicodePwd` is refused over plain LDAP. It needs LDAPS on
  636 with a DC certificate the client trusts, or Kerberos sign-and-seal on 389. This is the single most common
  Phase 2 setup failure and the error message must name it.
- **AD has no delta query.** There is `uSNChanged` polling and `DirSync`; neither resembles Graph's delta. The cache
  design assumes delta. On-prem objects likely need a different refresh strategy, not the same one.
- **Replication.** Write to one DC, read from another, see the old value. Pin a DC for the duration of an operation
  and read back from the same one.
- **`primaryGroupID`.** Removing a user from `Domain Users` through the members list silently does nothing; it is not
  a member, it is a primary group. Pre-check this one.
- **Protected from accidental deletion** sets a deny ACE. A delete or move fails with access denied, and the honest
  message names the checkbox rather than the ACE.
- **`userAccountControl` is a bit field.** Enable/disable is one bit of many; a read-modify-write that clobbers the
  others is a real way to break an account.

---

## 6. Architecture

Core is `net8.0` with no Windows dependency and 374 tests that must keep running anywhere. LDAP must not change that.

```
Core/Directory/
  IDirectoryService      the operations, in the console's vocabulary — not LDAP's
  DirectoryUser/Group/Computer/Ou    on-prem projections
  DirectoryError         LDAP result codes translated, as GraphErrorText does for Graph
  LdapDirectoryService   the implementation, System.DirectoryServices.Protocols
  FakeDirectoryService   in-memory, for tests and for a demo with no DC
```

Doc 03 §62 already anticipated this: *"Phase 2 adds `OnPremAdSource` alongside `EntraSource`; the User 360 merges by
`onPremisesImmutableId` ↔ `ms-DS-ConsistencyGuid`."*

`System.DirectoryServices.Protocols` is a NuGet package and CLAUDE.md requires asking before adding one. It is the
right choice over `System.DirectoryServices` (ADSI, Windows-only, COM) because it targets `net8.0`, does everything
needed including `ModifyDN` for moves and renames, and keeps the test suite runnable.

---

## 7. Testing without a customer's directory

A fake covers the logic. It does not and cannot cover LDAP semantics, and LDAP semantics are where the bugs are —
the same way this project's real Graph bugs (`$select=key` silently dropping the key, LAPS keyed on the wrong id,
`""` rejected for `department` but accepted for `officeLocation`) only ever surfaced against the live service.

So: **a disposable domain controller**, scripted and rebuildable.

`D:\Lab` on this machine holds a VirtualBox lab: Windows Server 2025 evaluation, `adlab.test`, a DC at
`192.168.56.10`, seeded with an OU tree, users with a manager chain, groups covering every scope and both categories
including a nested and an empty one, and computer objects in both states. Desktop Experience is installed so the
console can be run **on** the domain and exercise real integrated Kerberos, not only LDAP with explicit credentials.

The seed is chosen to force distinctions the console has to make: group scope and category have no Entra equivalent,
and nested and empty groups are where membership logic goes wrong.

---

## 8. Suggested milestones

| | |
|---|---|
| 12.1 | `IDirectoryService`, the projections, `DirectoryError`, and the fake. No LDAP, no package, all testable |
| 12.2 | `LdapDirectoryService`: DC discovery by SRV, Negotiate bind, connection lifetime, read a user by GUID |
| 12.3 | Read paths — users, groups, computers, the OU tree — and a Domain page showing what was found |
| 12.4 | The hybrid unlock: the 20 greyed attributes write to AD, with the sync-pending caption |
| 12.5 | Group membership and scope/category, with the `primaryGroupID` and protected-object pre-checks |
| 12.6 | Computers and OUs: move, enable/disable, LAPS from AD, delete with confirmation |
| 12.7 | The two waiting offboarding steps, un-greyed |
| 12.8 | Error translation pass over every LDAP result code the lab can be made to produce |

**Gate:** against the lab DC, perform every operation, then verify each one independently with `Get-AD*` rather than
by re-reading through the console — the same discipline that caught the Graph bugs, where reading back through the
thing that wrote it proves nothing.

---

## 9. Open questions

1. **Does the cache hold on-prem objects at all?** Caching them means solving change detection without delta.
   Reading AD live per page is simpler, and AD on a LAN is fast. Leaning live.
2. **AD-only objects in the palette.** A user who exists only in AD has no Entra id. Either the palette gains a
   source badge, or on-prem browsing lives only on a Domain page. The first is more useful and more work.
3. **Multiple domains and forests.** Out of scope for 12.x, but `IDirectoryService` should not assume one domain, or
   retrofitting it will be painful. *Answered in M20.3:* a tenant profile lists its domains, and `DirectoryRouter`
   puts several behind the one interface — reads merged, writes to the object's own domain, cross-domain moves and
   memberships refused with the reason (they are ADMT / `-TargetServer` operations, not LDAP modifies).

## 10. Turning it on for an installation (M12.4)

On-prem is off until a domain is named. The `OnPrem` section of `appsettings.json` — or `appsettings.local.json`,
which is gitignored — carries it:

```json
{
  "OnPrem": {
    "Domain": "corp.example.com"
  }
}
```

`Domain` alone is what a domain-joined machine wants: the LDAP client is handed the domain rather than a hostname, so
Windows finds a controller through DNS SRV and site topology, the bind is the admin's own Kerberos ticket, and a
retired DC strands nobody.

`Server` names one controller directly, which is needed only where the machine cannot resolve the domain — a
workstation outside the forest, or the lab — and when troubleshooting one DC in particular. `Credential`
(`UserName`, `Password`, `Domain`) exists for the same reason and should never be how a client deployment runs: it
puts a password on disk, and the whole point of non-negotiable 4 is that AD enforces what the signed-in admin has
actually been delegated.

With nothing configured, the Domain page says so and every mastered field on a synced user keeps the reason it had
before. Nothing else in the console changes.

### What the console asks the directory before offering an edit

`allowedAttributesEffective` on the target object, which the DC constructs per object and per caller. It answers both
"is this attribute in the forest's schema" and "has this admin been delegated it", and it is the only way to answer
either without attempting the write. A field that fails it stays greyed with that reason rather than accepting a
value and refusing it a moment later.
