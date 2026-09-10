# Entra Connect on the lab, and the sync into YeomanLabs Consulting

Why this exists: the hybrid unlock built in M12.4 has never run against a genuinely synced user. Every AD-side
operation is proven live and the branching is unit-tested, but the join it depends on — an Entra user's
`onPremisesImmutableId` pointing at a real AD `objectGUID` — has only ever been proven against the lab's own objects.
Nothing but a real Entra Connect can close that.

## What this changes that cannot be quietly changed back

Read this before signing in, because the wizard does not say it plainly.

- **Directory synchronisation becomes enabled on the tenant.** Turning it off afterwards is a PowerShell call
  (`Set-MsolDirSyncEnabled -EnableDirSync $false`) followed by a wait that Microsoft documents as up to 72 hours.
  It is not a checkbox.
- **The tenant gains a service account**, `Sync_DC01_<hex>@yeomanlab.onmicrosoft.com`, with directory-write rights.
  It stays until directory sync is disabled and it is removed by hand.
- **Any object that syncs becomes on-prem mastered.** Its display name, job title, department and the rest stop being
  editable in the cloud — which is the whole point of the exercise, and is also the thing to be careful about.
- **A soft match takes over an existing cloud account rather than creating a new one.** Entra Connect matches on
  primary SMTP address first and UPN second. A collision does not produce a duplicate; it converts a real person's
  account into a synced one.

## What is in place to contain it

- **One OU in scope.** `OU=CloudSync,OU=Lab,DC=adlab,DC=test` and nothing else. Not `Users`, not `Builtin`, not
  `Domain Controllers`, not the rest of `OU=Lab`. Everything the other M12 harness passes act on stays invisible to
  the sync.
- **Two purpose-made accounts in it**, `adlab.hybrid1` and `adlab.hybrid2`, created for this and disposable.
- **Neither can soft-match anything.** Checked against the tenant cache before any of this was installed: eleven real
  users, and no UPN or mail address that either lab account could match. They also carry no `proxyAddresses` at all,
  and an absent primary SMTP address cannot collide with a mailbox.
- **The lab UPN suffix `adlab.test` is not a verified domain**, so both accounts arrive as
  `adlab.hybrid1@yeomanlab.onmicrosoft.com`. They cannot land on `yeomanlabs.co`.
- **Password hash synchronisation off.** There is no reason to push lab password hashes into a live tenant.

## Two prerequisites the wizard checks, and one it does not

**TLS 1.2, which it does check.** Entra Connect 2.x refuses to install unless TLS 1.2 is switched on explicitly. On a
fresh Server 2022 none of the keys exist at all — TLS 1.2 works implicitly, so everything *appears* fine right up to
the point the prerequisite check stops you. Set on DC01:

- `SCHANNEL\Protocols\TLS 1.2\{Client,Server}` → `Enabled=1`, `DisabledByDefault=0`
- `.NETFramework\v4.0.30319` → `SystemDefaultTlsVersions=1`, `SchUseStrongCrypto=1`, in **both** the native and the
  `WOW6432Node` hive, because parts of the wizard are 32-bit

TLS 1.0 and 1.1 are deliberately left alone. The check asks whether 1.2 is on, not whether the old ones are off, and
turning them off on a domain controller has a blast radius well beyond this exercise.

SCHANNEL is read at boot, so this needs a restart. Verified afterwards by opening a socket to
`login.microsoftonline.com`, `graph.windows.net` and `aadcdn.msauth.net` offering TLS 1.2 only — all three completed
the handshake at `Tls12`/`Aes256`.

**The clock, which it does not check — and which fails much more confusingly.** After the restart DC01 came back
**70 minutes behind real time**. A domain controller holding the PDC emulator role sits at the top of the time
hierarchy with nothing above it to sync from, so it runs on the CMOS clock and drifts; the event log had been saying
so for hours. Kerberos tolerates five minutes of skew and OAuth token validation is no more forgiving, so the sign-in
would have failed with an error naming none of this.

```powershell
w32tm /config /manualpeerlist:"time.windows.com,0x9 pool.ntp.org,0x9" /syncfromflags:manual /reliable:yes /update
Restart-Service w32time
w32tm /resync /force
```

Now stratum 3 off `pool.ntp.org`, four seconds from real time. Worth re-checking with `w32tm /query /status` if the
VM has been paused or suspended for any length of time, since a suspended VM's clock stops with it.

## The wizard

Run it on DC01 from the VirtualBox console window. **You have to do the sign-in step**: it needs Global Administrator
credentials and MFA, and entering someone's credentials is not something the assistant does.

The installer is already downloaded and installed on the DC. Launch:

```
C:\Program Files\Microsoft Azure Active Directory Connect\AzureADConnect.exe
```

Choices, in order:

1. Licence terms — accept, **Continue**.
2. **Customize** (not Express — Express syncs the whole directory and turns on password hash sync).
3. Install required components — leave every optional box unticked. **Install**.
4. User sign-in — **Do not configure**. Then **Next**.
   *Pass-through authentication and password hash sync both change how sign-in works for the tenant. Neither is
   needed to prove the hybrid unlock.*
5. Connect to Azure AD — **this is your step.** Sign in as a Global Administrator of YeomanLabs Consulting.
6. Connect your directories — forest `adlab.test`, **Add Directory** → *Create new AD account* →
   `ADLAB\Administrator` / the lab password. **Next**.

   This one needs **Enterprise Admin**, not just Domain Admin, and `ADLAB\Administrator` is both — it is in Domain
   Admins, Enterprise Admins and Schema Admins. The reason is that the wizard creates an `MSOL_<hex>` service account
   in the forest and grants it replicate-directory-changes plus write access on the sync scope, which is a
   forest-level operation. The credential is used once and not stored: the `MSOL_` account is what runs the sync
   afterwards, with a random password Entra Connect manages itself.

   *A client deployment should not do it this way.* Pre-create a least-privilege account, run
   `Set-ADSyncRestrictedPermissions` against it, and hand that over instead, so no Enterprise Admin credential ever
   goes near the wizard. It is overkill for a disposable lab and wrong for anything else.

   Do not confuse this with step 5, which is the **Entra ID** Global Administrator. Different directory, different
   account.
7. Azure AD sign-in configuration — it will warn that `adlab.test` is not verified. **That warning is expected and
   is a guardrail**: it means these accounts get `.onmicrosoft.com` addresses and cannot take over anything on
   `yeomanlabs.co`. Tick *Continue without matching all UPN suffixes to verified domains*. **Next**.
8. Domain and OU filtering — **Sync selected domains and OUs**. Untick everything, then tick **only**
   `adlab.test → Lab → CloudSync`. **Next**.
   *This is the single most important screen. Everything else is recoverable; syncing the whole directory into a
   live tenant is the thing worth being slow about.*
9. Uniquely identifying your users — leave the defaults (`objectGUID` source anchor, or `mS-DS-ConsistencyGuid` if
   offered — take the consistency GUID where it is offered). **Next**.
10. Filter users and devices — *Synchronize all users and devices*. The OU filter already did the work. **Next**.
11. Optional features — **tick nothing**. No password hash sync, no writeback, no group writeback. **Next**.
12. Ready to configure — **tick "Enable staging mode"** for this first pass, then **Install**.

### Staging mode first, deliberately

Staging mode imports and works out every change without exporting any of it. It is the only way to see exactly what
would land in the tenant before anything does. When it finishes, run the check below; only when that reads correctly
do you take it out of staging.

## Verifying the staged run before anything reaches the tenant

On DC01:

```powershell
Import-Module ADSync
Start-ADSyncSyncCycle -PolicyType Initial
# wait for it to finish
Get-ADSyncScheduler
```

Then open **Synchronization Service Manager** (`C:\Program Files\Microsoft Azure AD Sync\UIShell\miisclient.exe`),
go to **Connectors → the Azure AD connector → Search Connector Space**, and look at what is pending export. It should
be two adds and nothing else. If it lists anything you did not expect — any object outside CloudSync, or any *update*
or *delete* against an existing cloud object — stop and do not leave staging mode.

## Leaving staging mode

Re-run `AzureADConnect.exe`, choose **Configure staging mode**, untick it, finish. Then force a sync:

```powershell
Import-Module ADSync
Start-ADSyncSyncCycle -PolicyType Initial
```

Within a few minutes both accounts appear in the tenant as synced users. In the console they will show the **Synced**
badge, and the Identity tab's twenty mastered fields become editable and write to AD — which is the thing this was
all for.

## Undoing it

In rough order of how much you have to care:

1. Delete the two accounts from `OU=CloudSync` and let a sync cycle remove them from the tenant.
2. Uninstall Entra Connect from DC01 (Programs and Features — it removes the sync engine and its LocalDB).
3. Disable directory synchronisation on the tenant, which is the slow one:
   ```powershell
   Connect-MgGraph -Scopes Organization.ReadWrite.All
   Update-MgOrganization -OrganizationId <tenantId> -OnPremisesSyncEnabled:$false
   ```
   Expect the tenant to take hours to stop treating the objects as on-prem mastered.
4. Remove the `Sync_DC01_*` service account from the tenant.
