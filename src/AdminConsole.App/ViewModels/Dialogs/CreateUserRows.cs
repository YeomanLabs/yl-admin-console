using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Models;
using AdminConsole.Core.Templates;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// One licence on the create form (doc 07 §flow step 2). Seat counts come from the cached SKU list, and a SKU with no
/// seats left is unticked and locked with the reason rather than failing at the end of the run.
/// </summary>
public sealed partial class CreateLicenseRow : ObservableObject
{
    public CreateLicenseRow(LicenseSku sku, bool selected, Action changed)
    {
        Sku = sku;
        _isSelected = selected && sku.Available > 0;
        _changed = changed;
    }

    private readonly Action _changed;

    public LicenseSku Sku { get; }
    public string SkuId => Sku.SkuId;
    public string Name => Sku.DisplayName;
    public string SeatsText => $"{Sku.Available:N0} of {Sku.Enabled:N0} available";

    public bool HasSeats => Sku.Available > 0;
    public string? BlockedReason => HasSeats ? null : "No seats left on this licence";
    public bool HasBlockedReason => BlockedReason is not null;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

/// <summary>One group chip on the create form. Removable; the picker adds more.</summary>
public sealed partial class CreateGroupRow : ObservableObject
{
    public CreateGroupRow(TemplateGroup group, Action<CreateGroupRow> remove)
    {
        Group = group;
        _remove = remove;
    }

    private readonly Action<CreateGroupRow> _remove;

    public TemplateGroup Group { get; }
    public string Id => Group.Id;
    public string Label => Group.DisplayName ?? Group.Id;

    [RelayCommand]
    private void Remove() => _remove(this);
}

/// <summary>One editable attribute on the create form: the template's value, ready to be overridden.</summary>
public sealed partial class CreateFieldRow : ObservableObject
{
    public CreateFieldRow(string label, string propertyName, string? value)
    {
        Label = label;
        PropertyName = propertyName;
        _value = value ?? string.Empty;
    }

    public string Label { get; }
    public string PropertyName { get; }

    [ObservableProperty]
    private string _value;

    public string? Trimmed => Value.Trim() is { Length: > 0 } v ? v : null;
}
