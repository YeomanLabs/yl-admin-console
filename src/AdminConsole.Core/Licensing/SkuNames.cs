namespace AdminConsole.Core.Licensing;

/// <summary>
/// Static <c>skuPartNumber</c> → display name map (doc 04 §Licenses). Unknown SKUs show the part number.
/// Extend as encountered; there is no Graph endpoint for these names.
/// </summary>
public static class SkuNames
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SPE_E3"] = "Microsoft 365 E3",
        ["SPE_E5"] = "Microsoft 365 E5",
        ["SPE_F1"] = "Microsoft 365 F3",
        ["Microsoft_365_E3_(no_Teams)"] = "Microsoft 365 E3 (no Teams)",
        ["Microsoft_365_E5_(no_Teams)"] = "Microsoft 365 E5 (no Teams)",
        ["ENTERPRISEPACK"] = "Office 365 E3",
        ["ENTERPRISEPREMIUM"] = "Office 365 E5",
        ["STANDARDPACK"] = "Office 365 E1",
        ["DESKLESSPACK"] = "Office 365 F3",
        ["O365_BUSINESS_ESSENTIALS"] = "Microsoft 365 Business Basic",
        ["O365_BUSINESS_PREMIUM"] = "Microsoft 365 Business Standard",
        ["SPB"] = "Microsoft 365 Business Premium",
        ["EMS"] = "Enterprise Mobility + Security E3",
        ["EMSPREMIUM"] = "Enterprise Mobility + Security E5",
        ["AAD_PREMIUM"] = "Microsoft Entra ID P1",
        ["AAD_PREMIUM_P2"] = "Microsoft Entra ID P2",
        ["INTUNE_A"] = "Microsoft Intune Plan 1",
        ["EXCHANGESTANDARD"] = "Exchange Online (Plan 1)",
        ["EXCHANGEENTERPRISE"] = "Exchange Online (Plan 2)",
        ["EXCHANGEDESKLESS"] = "Exchange Online Kiosk",
        ["WIN10_PRO_ENT_SUB"] = "Windows 10/11 Enterprise E3",
        ["WIN10_VDA_E5"] = "Windows 10/11 Enterprise E5",
        ["Microsoft_365_Copilot"] = "Microsoft 365 Copilot",
        ["Microsoft_Teams_Enterprise_New"] = "Microsoft Teams Enterprise",
        ["Microsoft_Teams_EEA_New"] = "Microsoft Teams EEA",
        ["TEAMS_EXPLORATORY"] = "Microsoft Teams Exploratory",
        ["MCOMEETADV"] = "Microsoft 365 Audio Conferencing",
        ["MCOEV"] = "Microsoft Teams Phone Standard",
        ["PHONESYSTEM_VIRTUALUSER"] = "Microsoft Teams Phone Resource Account",
        ["VISIOCLIENT"] = "Visio Plan 2",
        ["PROJECTPROFESSIONAL"] = "Project Plan 3",
        ["PROJECTPREMIUM"] = "Project Plan 5",
        ["POWER_BI_PRO"] = "Power BI Pro",
        ["POWER_BI_STANDARD"] = "Power BI (free)",
        ["FLOW_FREE"] = "Power Automate Free",
        ["POWERAPPS_VIRAL"] = "Power Apps Plan 2 Trial",
        ["STREAM"] = "Microsoft Stream Trial",
        ["DEFENDER_ENDPOINT_P1"] = "Microsoft Defender for Endpoint P1",
        ["WINDEFATP"] = "Microsoft Defender for Endpoint P2",
        ["ATP_ENTERPRISE"] = "Microsoft Defender for Office 365 (Plan 1)",
        ["THREAT_INTELLIGENCE"] = "Microsoft Defender for Office 365 (Plan 2)",
        ["RIGHTSMANAGEMENT"] = "Azure Information Protection Plan 1",
        ["CCIBOTS_PRIVPREV_VIRAL"] = "Power Virtual Agents Viral Trial",
        ["Microsoft_Cloud_App_Security_App_Discovery"] = "Defender for Cloud Apps App Discovery",
        ["RMSBASIC"] = "Rights Management Service Basic",
        ["DYN365_ENTERPRISE_PLAN1"] = "Dynamics 365 Customer Engagement Plan",
        ["MICROSOFT_BUSINESS_CENTER"] = "Microsoft Business Center",
        ["Windows_365_Business_2vCPU_4GB_128GB"] = "Windows 365 Business 2 vCPU 4 GB 128 GB",
    };

    /// <summary>Friendly name, or null when unknown (callers fall back to the part number).</summary>
    public static string? For(string? skuPartNumber) =>
        skuPartNumber is not null && Map.TryGetValue(skuPartNumber, out var name) ? name : null;
}
