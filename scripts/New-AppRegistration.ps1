<#
.SYNOPSIS
    Creates the Entra app registration the YeomanLabs Admin Console signs in with, in whichever tenant you connect to.

.DESCRIPTION
    Replaces the manual click-through in docs/08-APP-REGISTRATION.md. It creates a public-client registration, adds the
    redirect URIs the Windows broker needs, requests every delegated Graph permission the console uses, creates the
    service principal, and (unless told not to) grants admin consent for the tenant.

    The registration belongs to the tenant it is created in. Nothing outside that tenant gains access, and the app is
    signed in as the admin using it — delegated permissions only, no client secret, no application permissions.

    Run it with -WhatIf first. That signs in and resolves every permission against Microsoft Graph but creates nothing,
    which is enough to prove the permission list is valid before anything is written.

.PARAMETER Name
    Display name for the registration. Defaults to "YeomanLabs Admin Console".

.PARAMETER TenantId
    Tenant to create it in — a GUID or a domain like contoso.onmicrosoft.com. Omit to be prompted at sign-in.

.PARAMETER SingleTenant
    Restrict sign-in to this tenant only. The default is multi-tenant, which is what lets one build serve any tenant;
    single-tenant is tighter and is the right choice for a client who will only ever use their own.

.PARAMETER NoConsent
    Create the registration but do not grant admin consent. The script prints the consent URL instead, for when the
    person running it cannot consent themselves.

.PARAMETER WriteConfig
    Write the client ID straight into this machine's tenant.json, so the console picks it up with nothing to paste.

.PARAMETER Force
    Create another registration even if one with the same name already exists.

.EXAMPLE
    .\New-AppRegistration.ps1 -WhatIf
    Signs in, resolves every permission, and reports what it would create. Changes nothing.

.EXAMPLE
    .\New-AppRegistration.ps1 -TenantId contoso.onmicrosoft.com -SingleTenant -WriteConfig
    Creates it in Contoso, restricted to Contoso, and points this machine's console at it.

.EXAMPLE
    .\New-AppRegistration.ps1 -Confirm:$false
    Skips the confirmation prompt. Required when running non-interactively: with no console to prompt on,
    ShouldProcess has nowhere to ask.

.NOTES
    Needs the Microsoft.Graph PowerShell module and an account holding Application Administrator (to create the
    registration) and Privileged Role Administrator or Global Administrator (to grant admin consent).
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Name = 'YeomanLabs Admin Console',
    [string]$TenantId,
    [switch]$SingleTenant,
    [switch]$NoConsent,
    [switch]$WriteConfig,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Microsoft Graph's own service principal. Constant across every tenant.
$GraphAppId = '00000003-0000-0000-c000-000000000000'

# --- The permissions the console uses -------------------------------------------------------------------------------
# These must match Core/Auth/Scopes.cs exactly. A test (ScriptScopesMatchCodeTests) fails the build if they drift, so
# do not edit one without the other.
#
# Read tier — consented at first sign-in.
$ReadScopes = @(
    'openid'
    'profile'
    'offline_access'
    'User.Read'
    'User.Read.All'
    'Group.Read.All'
    'Device.Read.All'
    'Organization.Read.All'
    'DeviceManagementManagedDevices.Read.All'
    'DeviceManagementConfiguration.Read.All'
    'UserAuthenticationMethod.Read.All'
    'AuditLog.Read.All'
)
# Write tier — asked for the first time the admin changes something, not up front.
$WriteScopes = @(
    'User.ReadWrite.All'
    'User-PasswordProfile.ReadWrite.All'
    'User.RevokeSessions.All'
    'GroupMember.ReadWrite.All'
    'Group.ReadWrite.All'
    'UserAuthenticationMethod.ReadWrite.All'
    'DeviceManagementManagedDevices.ReadWrite.All'
    'DeviceManagementManagedDevices.PrivilegedOperations.All'
)
# Sensitive tier — asked for only when a reveal button is clicked.
$SensitiveScopes = @(
    'BitlockerKey.Read.All'
    'DeviceLocalCredential.Read.All'
)
$AllScopes = $ReadScopes + $WriteScopes + $SensitiveScopes

function Write-Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "   $text" -ForegroundColor Green }
function Write-Info($text) { Write-Host "   $text" }
function Write-Warn($text) { Write-Host "   $text" -ForegroundColor Yellow }

# --- Prerequisites --------------------------------------------------------------------------------------------------
Write-Step 'Checking prerequisites'
foreach ($module in 'Microsoft.Graph.Authentication', 'Microsoft.Graph.Applications') {
    if (-not (Get-Module -ListAvailable -Name $module)) {
        throw "The $module module is not installed. Install it with:`n" +
              "    Install-Module Microsoft.Graph -Scope CurrentUser`n" +
              'and run this again.'
    }
}
# Import-Module has no -WhatIf of its own, but the module's internal Update-TypeData calls honour the inherited
# preference and bury the report under fifteen lines of "What if: Performing the operation Update TypeData".
# Suppressing it around the imports is the only way to load quietly under -WhatIf.
$previousWhatIf = $WhatIfPreference
$WhatIfPreference = $false
try {
    Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
    Import-Module Microsoft.Graph.Applications -ErrorAction Stop
}
finally { $WhatIfPreference = $previousWhatIf }
Write-Ok 'Microsoft.Graph modules present'

# --- Sign in --------------------------------------------------------------------------------------------------------
Write-Step 'Signing in'
$connect = @{
    # Enough to create the registration and to grant consent. Nothing application-only.
    Scopes    = @('Application.ReadWrite.All', 'DelegatedPermissionGrant.ReadWrite.All', 'Directory.Read.All')
    NoWelcome = $true
}
if ($TenantId) { $connect.TenantId = $TenantId }
Connect-MgGraph @connect | Out-Null

$context = Get-MgContext
if (-not $context) { throw 'Sign-in did not complete.' }
$org = Invoke-MgGraphRequest -Method GET -Uri 'v1.0/organization?$select=id,displayName,verifiedDomains'
$tenantName = $org.value[0].displayName
$primaryDomain = ($org.value[0].verifiedDomains | Where-Object { $_.isDefault }).name
Write-Ok "Signed in to $tenantName ($primaryDomain) as $($context.Account)"

# --- Resolve the permissions before creating anything -----------------------------------------------------------------
# Resolving first means a typo or an application-only permission fails here, with nothing half-created behind it.
Write-Step 'Resolving permissions against Microsoft Graph'
$graphSp = Invoke-MgGraphRequest -Method GET -Uri "v1.0/servicePrincipals(appId='$GraphAppId')?`$select=id,appId,oauth2PermissionScopes"
$available = @{}
foreach ($scope in $graphSp.oauth2PermissionScopes) { $available[$scope.value.ToLowerInvariant()] = $scope.id }

$resolved = @()
$missing = @()
foreach ($scope in $AllScopes) {
    $id = $available[$scope.ToLowerInvariant()]
    if ($id) { $resolved += [pscustomobject]@{ Scope = $scope; Id = $id } }
    else     { $missing += $scope }
}

if ($missing.Count -gt 0) {
    Write-Warn "These are not delegated permissions on Microsoft Graph: $($missing -join ', ')"
    throw 'Cannot continue: every permission this console uses must be delegated. An application-only permission ' +
          'here would mean the console could act without a signed-in admin, which it is designed never to do.'
}
Write-Ok "All $($resolved.Count) delegated permissions resolved ($($ReadScopes.Count) read, $($WriteScopes.Count) write, $($SensitiveScopes.Count) sensitive)"

# --- Refuse to make a duplicate silently ------------------------------------------------------------------------------
$escaped = $Name.Replace("'", "''")
$existing = (Invoke-MgGraphRequest -Method GET -Uri "v1.0/applications?`$filter=displayName eq '$escaped'&`$select=id,appId,displayName").value
if ($existing.Count -gt 0) {
    Write-Warn "A registration called '$Name' already exists in this tenant:"
    foreach ($app in $existing) { Write-Warn "     $($app.appId)" }
    if (-not $Force) {
        Write-Warn 'Use that client ID, or pass -Force to create another, or -Name to use a different name.'
        if (-not $WhatIfPreference) { return }
    }
}

$audience = if ($SingleTenant) { 'AzureADMyOrg' } else { 'AzureADMultipleOrgs' }

if ($WhatIfPreference) {
    Write-Step 'What would happen'
    Write-Info "Tenant            : $tenantName ($primaryDomain)"
    Write-Info "Registration      : $Name"
    Write-Info "Sign-in audience  : $audience"
    Write-Info "Public client     : yes (no secret, Windows broker)"
    Write-Info "Permissions       : $($resolved.Count) delegated, all resolved"
    Write-Info "Admin consent     : $(if ($NoConsent) { 'no — a URL would be printed' } else { 'yes' })"
    Write-Host "`nNothing was created. Run again without -WhatIf to do it." -ForegroundColor Cyan
    return
}

# --- Create the registration ------------------------------------------------------------------------------------------
Write-Step "Creating the registration in $tenantName"
# ConfirmImpact is High, so this prompts by default — creating a registration and granting tenant-wide consent should
# not happen because somebody mistyped a command. With no interactive host to prompt on, ShouldProcess throws a bare
# null reference rather than saying why, so say why.
try {
    if (-not $PSCmdlet.ShouldProcess("$Name in $tenantName", 'Create app registration')) { return }
}
catch [System.NullReferenceException] {
    throw 'This session cannot show a confirmation prompt. Re-run with -Confirm:$false if you are sure, or run it ' +
          'from an interactive PowerShell window.'
}

$body = @{
    displayName            = $Name
    signInAudience         = $audience
    # A public client: it holds no secret, and MSAL signs the admin in through the Windows broker.
    isFallbackPublicClient = $true
    requiredResourceAccess = @(@{
        resourceAppId  = $GraphAppId
        resourceAccess = @($resolved | ForEach-Object { @{ id = $_.Id; type = 'Scope' } })
    })
    publicClient           = @{
        redirectUris = @(
            'https://login.microsoftonline.com/common/oauth2/nativeclient'
            'http://localhost'
        )
    }
}
$app = Invoke-MgGraphRequest -Method POST -Uri 'v1.0/applications' -Body ($body | ConvertTo-Json -Depth 10)
Write-Ok "Created — client ID $($app.appId)"

# The broker redirect URI contains the client ID, which does not exist until the app does. Hence the second call.
Write-Step 'Adding the Windows broker redirect URI'
$redirects = @(
    "ms-appx-web://microsoft.aad.brokerplugin/$($app.appId)"
    'https://login.microsoftonline.com/common/oauth2/nativeclient'
    'http://localhost'
)
Invoke-MgGraphRequest -Method PATCH -Uri "v1.0/applications/$($app.id)" `
    -Body (@{ publicClient = @{ redirectUris = $redirects } } | ConvertTo-Json -Depth 10) | Out-Null
Write-Ok 'Broker redirect URI added'

Write-Step 'Creating the service principal'
$sp = Invoke-MgGraphRequest -Method POST -Uri 'v1.0/servicePrincipals' -Body (@{ appId = $app.appId } | ConvertTo-Json)
Write-Ok "Service principal $($sp.id)"

# --- Admin consent ------------------------------------------------------------------------------------------------
$consentUrl = "https://login.microsoftonline.com/$($context.TenantId)/adminconsent?client_id=$($app.appId)"
if ($NoConsent) {
    Write-Step 'Admin consent not granted (-NoConsent)'
    Write-Info 'Someone who can consent for the tenant must open:'
    Write-Info $consentUrl
}
else {
    Write-Step 'Granting admin consent for the tenant'
    try {
        # One grant covers every delegated permission for all users; the console still asks the admin for the write
        # and sensitive tiers at the moment it needs them, so consent here does not mean it uses them straight away.
        Invoke-MgGraphRequest -Method POST -Uri 'v1.0/oauth2PermissionGrants' -Body (@{
            clientId    = $sp.id
            consentType = 'AllPrincipals'
            resourceId  = $graphSp.id
            scope       = ($AllScopes -join ' ')
        } | ConvertTo-Json) | Out-Null
        Write-Ok 'Admin consent granted'
    }
    catch {
        Write-Warn "Could not grant consent automatically: $($_.Exception.Message)"
        Write-Warn 'The registration exists and is fine. Someone who can consent for the tenant should open:'
        Write-Warn $consentUrl
    }
}

# --- Hand back the client ID ----------------------------------------------------------------------------------------
if ($WriteConfig) {
    $configDir = Join-Path $env:LOCALAPPDATA 'YeomanLabs\AdminConsole'
    $configPath = Join-Path $configDir 'tenant.json'
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    $authority = if ($SingleTenant) { "https://login.microsoftonline.com/$($context.TenantId)" }
                 else { 'https://login.microsoftonline.com/organizations' }
    @{
        clientId   = $app.appId
        authority  = $authority
        tenantHint = $primaryDomain
    } | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
    Write-Step 'Configuration written'
    Write-Ok $configPath
}

Write-Host "`n" -NoNewline
Write-Host '  Done.' -ForegroundColor Green
Write-Host "  Tenant     : $tenantName ($primaryDomain)"
Write-Host "  Client ID  : $($app.appId)" -ForegroundColor Cyan
Write-Host "  Audience   : $audience"
if (-not $WriteConfig) {
    Write-Host "`n  Start the console and paste that client ID into the setup screen."
}
Write-Host "  To undo everything this created: Remove-MgApplication -ApplicationId $($app.id)`n"
