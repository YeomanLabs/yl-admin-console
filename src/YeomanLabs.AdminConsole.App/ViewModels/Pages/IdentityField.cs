namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>One row of the Identity property sheet (UX §4 tab 1). Read-only in M4; M5.2 adds edit-on-blur.</summary>
public sealed record IdentityField(string Label, string PropertyName, string? Value, bool IsMono = false, bool IsReadOnly = false, string? ReadOnlyReason = null)
{
    public string Display => string.IsNullOrEmpty(Value) ? "—" : Value;
    public bool HasValue => !string.IsNullOrEmpty(Value);
    public bool HasReadOnlyReason => !string.IsNullOrEmpty(ReadOnlyReason);
}
