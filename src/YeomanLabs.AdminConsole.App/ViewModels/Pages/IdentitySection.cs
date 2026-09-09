namespace YeomanLabs.AdminConsole.App.ViewModels.Pages;

/// <summary>A titled group of identity fields (Name, Job, Contact, Address, Settings).</summary>
public sealed record IdentitySection(string Title, IReadOnlyList<IdentityField> Fields);
