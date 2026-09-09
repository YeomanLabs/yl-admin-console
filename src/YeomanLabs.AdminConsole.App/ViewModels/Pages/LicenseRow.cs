using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YeomanLabs.AdminConsole.Core.Licensing;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Writes;

namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One tenant SKU on the Access tab (UX §4 tab 2): checked when assigned, with seat counts and how it was assigned.
/// Toggling assigns or removes immediately with an undo toast (UX §10); group-assigned rows are locked with the reason.
/// </summary>
public sealed partial class LicenseRow : ObservableObject
{
    private readonly Func<LicenseRow, bool, Task<WriteOutcome>> _toggle;

    public LicenseRow(LicenseSku sku, bool isAssigned, LicenseAssignmentState? state, string? blockedReason, Func<LicenseRow, bool, Task<WriteOutcome>> toggle)
    {
        Sku = sku;
        State = state;
        _toggle = toggle;
        _isAssigned = isAssigned;
        BlockedReason = LicenseService.RemovalBlockedReason(state) is { } groupReason && isAssigned ? groupReason : blockedReason;
    }

    public LicenseSku Sku { get; }
    public LicenseAssignmentState? State { get; }

    public string SkuId => Sku.SkuId;
    public string Name => Sku.DisplayName;
    public string PartNumber => Sku.SkuPartNumber;
    public string SeatsText => $"{Sku.Available:N0} of {Sku.Enabled:N0} available";
    public bool IsGroupAssigned => State?.IsGroupAssigned == true;
    public bool HasError => State?.HasError == true;
    public string? ErrorText => HasError ? State!.Error : null;

    /// <summary>UX §6: why this row is not toggleable — group-assigned, missing consent, or not built yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToggleable))]
    [NotifyPropertyChangedFor(nameof(HasBlockedReason))]
    private string? _blockedReason;

    public bool IsToggleable => BlockedReason is null && !IsBusy;
    public bool HasBlockedReason => BlockedReason is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToggleable))]
    private bool _isBusy;

    /// <summary>Two-way bound to the checkbox. Setting it fires the write and reverts on failure.</summary>
    [ObservableProperty]
    private bool _isAssigned;

    partial void OnIsAssignedChanged(bool value)
    {
        if (_suppress) return;
        _ = ApplyAsync(value);
    }

    private bool _suppress;

    private async Task ApplyAsync(bool assign)
    {
        if (BlockedReason is not null)
        {
            Set(!assign);
            return;
        }

        IsBusy = true;
        try
        {
            var outcome = await _toggle(this, assign);
            if (!outcome.Success) Set(!assign);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Sets the checkbox without triggering another write (failure revert, undo, refresh).</summary>
    public void Set(bool assigned)
    {
        _suppress = true;
        IsAssigned = assigned;
        _suppress = false;
    }
}
