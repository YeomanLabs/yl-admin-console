using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One tenant SKU on the Access tab (UX §4 tab 2): checked when assigned to the user, with seat counts and how it was
/// assigned. Read-only in M4; M5.3 makes the checkbox toggle assign/remove.
/// </summary>
public sealed record LicenseRow(LicenseSku Sku, bool IsAssigned, LicenseAssignmentState? State)
{
    public string Name => Sku.DisplayName;
    public string PartNumber => Sku.SkuPartNumber;
    public string SeatsText => $"{Sku.Available:N0} of {Sku.Enabled:N0} available";
    public bool IsGroupAssigned => State?.IsGroupAssigned == true;
    public bool HasError => State?.HasError == true;
    public string? ErrorText => HasError ? State!.Error : null;

    /// <summary>UX §6 grey rule for group-based licenses: cannot be removed per user.</summary>
    public string? Caption => IsGroupAssigned ? "Assigned by group — remove via the group" : null;
}
