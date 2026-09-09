namespace YeomanLabs.AdminConsole.Core.Graph;

/// <summary>
/// The exact <c>$select</c> lists from docs/04-GRAPH-REFERENCE.md. Keep each in one place so the cache projection,
/// the delta walk and the live detail refresh never drift apart.
/// </summary>
public static class GraphSelects
{
    /// <summary>Full user select, including <c>signInActivity</c> (P1 + AuditLog.Read.All; retry without it on 403/400).</summary>
    public const string User =
        "id,displayName,givenName,surname,userPrincipalName,mail,mailNickname,otherMails,proxyAddresses," +
        "accountEnabled,userType,createdDateTime,jobTitle,department,companyName,officeLocation," +
        "employeeId,employeeType,employeeHireDate,businessPhones,mobilePhone,streetAddress,city,state," +
        "postalCode,country,usageLocation,preferredLanguage,onPremisesSyncEnabled,onPremisesImmutableId," +
        "onPremisesDistinguishedName,onPremisesSamAccountName,onPremisesLastSyncDateTime," +
        "onPremisesProvisioningErrors,assignedLicenses,assignedPlans,lastPasswordChangeDateTime," +
        "signInActivity";

    /// <summary><see cref="User"/> minus <c>signInActivity</c>, which <c>/users/delta</c> does not support.</summary>
    public const string UserDelta =
        "id,displayName,givenName,surname,userPrincipalName,mail,mailNickname,otherMails,proxyAddresses," +
        "accountEnabled,userType,createdDateTime,jobTitle,department,companyName,officeLocation," +
        "employeeId,employeeType,employeeHireDate,businessPhones,mobilePhone,streetAddress,city,state," +
        "postalCode,country,usageLocation,preferredLanguage,onPremisesSyncEnabled,onPremisesImmutableId," +
        "onPremisesDistinguishedName,onPremisesSamAccountName,onPremisesLastSyncDateTime," +
        "onPremisesProvisioningErrors,assignedLicenses,assignedPlans,lastPasswordChangeDateTime";

    public const string Group =
        "id,displayName,description,mail,mailEnabled,mailNickname,securityEnabled,groupTypes,membershipRule," +
        "membershipRuleProcessingState,onPremisesSyncEnabled,visibility,createdDateTime,isAssignableToRole";

    /// <summary>Entra device objects (<c>/devices</c>), not Intune.</summary>
    public const string Device =
        "id,deviceId,displayName,operatingSystem,operatingSystemVersion,trustType,isCompliant,isManaged,accountEnabled," +
        "approximateLastSignInDateTime,registrationDateTime,manufacturer,model,enrollmentType,managementType,profileType";

    /// <summary>Intune <c>/deviceManagement/managedDevices</c>. No delta; full walk on an interval.</summary>
    public const string ManagedDevice =
        "id,deviceName,userId,userPrincipalName,userDisplayName,azureADDeviceId,serialNumber,manufacturer,model," +
        "operatingSystem,osVersion,complianceState,isEncrypted,lastSyncDateTime,enrolledDateTime,managementAgent," +
        "deviceEnrollmentType,enrollmentProfileName,azureADRegistered,managedDeviceOwnerType,totalStorageSpaceInBytes," +
        "freeStorageSpaceInBytes,physicalMemoryInBytes,wiFiMacAddress,ethernetMacAddress,emailAddress,deviceCategoryDisplayName";

    public const string Organization =
        "id,displayName,verifiedDomains,onPremisesSyncEnabled,onPremisesLastSyncDateTime,assignedPlans,countryLetterCode";

    public const string Me = "id,displayName,userPrincipalName";

    public const string DirectoryRole = "displayName,roleTemplateId";

    /// <summary>For <c>/users/{id}/memberOf</c>; includes directoryRoles, filter by <c>@odata.type</c>.</summary>
    public const string MemberOf = "id,displayName,groupTypes,mailEnabled,securityEnabled,onPremisesSyncEnabled,membershipRule";

    public const string SubscribedSku = "skuId,skuPartNumber,prepaidUnits,consumedUnits,capabilityStatus,servicePlans";

    public const string PersonRef = "id,displayName,userPrincipalName";
}
