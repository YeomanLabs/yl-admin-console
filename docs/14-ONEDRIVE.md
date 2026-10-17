# 14 — OneDrive (SharePoint Online)

M16. A user's OneDrive is a SharePoint site collection. The console reads and changes it through the SharePoint
**tenant admin API** — which needs no access to the OneDrive itself — and reads its files through **Graph**. Every call
below was proven against YeomanLabs on 2026-10-17 with the console's own delegated sign-in; the tenant admin endpoints
are undocumented and carry no SLA (PnPjs says the same), so this page is the record of exactly what was sent.

## 1. Sign-in and hosts

- **One scope for every tenant:** `00000003-0000-0ff1-ce00-000000000000/AllSites.FullControl` — SharePoint addressed by
  its application id rather than a host. The token's audience is the app id, and SharePoint accepts it on the admin host,
  the OneDrive host and the root host alike, CSOM included. So the scope is a constant and needs no knowledge of the
  tenant's SharePoint name. `Scopes.ResourceOf` treats `{guid}/` as a resource prefix for consent grouping.
- **Graph:** `Files.Read.All` for the export, asked for at the first export (the "files" tier).
- **Hosts:** `{name}.sharepoint.com`, `{name}-admin`, `{name}-my`. The name is the tenant's initial
  `*.onmicrosoft.com` domain (not the hybrid `*.mail.onmicrosoft.com`); a OneDrive URL is the authoritative source and is
  preferred when there is one (`SharePointHosts`).
- **Not possible delegated:** pre-creating a OneDrive. `SP.UserProfiles.ProfileLoader.GetProfileLoader/CreatePersonalSiteEnqueueBulk`
  answered 403 `UnauthorizedAccessException` on the admin host and on the OneDrive host, with and without SharePoint's
  `User.ReadWrite.All`, for a Global Administrator. A OneDrive exists only once its owner opens it; the tab says so.

## 2. Reads

| What | Call | Notes |
|---|---|---|
| Where it is | Graph `GET users/{id}?$select=mySite` | null → no OneDrive yet. Strip the trailing slash. |
| Settings | `POST {admin}/_api/SPO.Tenant/GetSitePropertiesByUrl` `{"url":"…","includeDetail":true}` | A trailing slash fails with "not a managed path". `StorageUsage`/`StorageMaximumLevel` in MB (numbers or strings), `SharingCapability` 0–3, `LockState`, `Owner`, `SiteId`, `LastContentModifiedDate` (UTC, no zone). |
| Site admins | `POST {admin}/_api/SPO.Tenant/GetSiteAdministrators` `{"siteId":"…"}` | `value[]` of `loginName`, `name`, `email`, `userPrincipalName`. |
| Tenant ceiling | `GET {admin}/_api/SPO.Tenant` | `OneDriveSharingCapability` when present, else `SharingCapability`. No OneDrive may be more open. |
| Every OneDrive | `POST {admin}/_api/SPO.Tenant/GetSitePropertiesFromSharePointByFilters` `{"speFilter":{"IncludePersonalSite":1,"Template":"SPSPERS"}}` | Used in probing only (M19 reports may use it). |

`SharingCapability`: 0 organisation only, 3 existing guests, 1 new and existing guests, 2 anyone — in that order of
openness, which is not numeric order.

## 3. Writes

| What | Call |
|---|---|
| Lock / unlock | `PATCH {admin}/_api/Microsoft.Online.SharePoint.TenantAdministration.Tenant/Sites('{siteId}')` `{"LockState":"ReadOnly"}` → 204 |
| Sharing | the same, `{"SharingCapability":1}` → 204 (`POST` with `X-HTTP-Method: MERGE` and odata=verbose also works) |
| Site admin add / remove | CSOM over raw HTTP: `POST {admin}/_vti_bin/client.svc/ProcessQuery`, `text/xml`, `Tenant.SetSiteAdmin(siteUrl, loginName, isSiteAdmin)` with `<Constructor TypeId="{268004ae-ef6b-4e9b-8425-127220d84719}"/>` |

- **A locked site refuses everything else.** A sharing change on a read-only site answers 403; `SetSiteAdmin` answers
  "Additions to this Web site have been blocked" inside a 200. Unlock first, on its own — a PATCH carrying both
  `LockState` and another field while locked is refused whole.
- **CSOM reports failure inside a 200:** element 0's `ErrorInfo` (`ErrorMessage`, `ErrorCode`, `ErrorTypeName`).
- **Login names are claims:** `i:0#.f|membership|{upn}`. A disabled account answers "The user does not exist or is not
  unique."
- `SetSiteAdmin` is used rather than `SPOInternalUseOnly.Tenant/SetSiteSecondaryAdministrators`, which also works but
  replaces the whole list and is named for internal use.
- Every write reads the site back (0.5, 1.5, 3, 5 s) before it is reported; in the gate each showed on the first read.

## 4. Export (M16.4)

1. If the signed-in admin is not a site admin, `SetSiteAdmin` makes them one for the export and takes them off afterwards,
   whatever happens — both in the action log. `Files.Read.All` alone does not open someone else's OneDrive.
2. Graph `GET users/{id}/drive?$select=id`, retried on 403/404 while the new access propagates.
3. Graph `GET drives/{id}/root/delta?$select=id,name,size,file,folder,parentReference,deleted,lastModifiedDateTime`, all
   pages: one walk lists every file with its folder path (`parentReference.path` after `root:`, URL-decoded) and
   `file.hashes.quickXorHash`.
4. Each file: `GET drives/{id}/items/{item}/content` → 302 to a pre-authenticated URL, streamed to
   `{zip}.parts\{n}.bin` while its QuickXorHash is computed. A mismatch is fetched once more, then reported.
5. A part already on disk with the right size and hash is kept — that is the resume.
6. Only when every file is down and checked: the ZIP is written from the parts to `{zip}.tmp` and moved into place, and
   the parts are deleted. A ZIP that exists is a complete one.

QuickXorHash is Microsoft's published algorithm; the implementation reproduces the hash SharePoint reported for a file
uploaded in the gate.

## 5. Where it shows

- **OneDrive tab** on User 360: URL (copy), usage bar, last change, lock picker, sharing picker capped at the tenant,
  site admins with remove (owner excluded), Add admin…, Export to ZIP… with progress in the tab and the status bar.
- **Offboarding:** `delegate-onedrive` (step 15, off by default) makes the delegate a site admin; `export-onedrive` (off)
  writes `{folder}\{upn} OneDrive {date}.zip` to the profile's `exportFolder`. The snapshot records URL, lock, sharing and
  admins; `restore-onedrive` unlocks first, then puts sharing and admins back.
