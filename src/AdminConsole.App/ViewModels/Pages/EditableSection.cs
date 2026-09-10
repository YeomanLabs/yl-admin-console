namespace AdminConsole.App.ViewModels.Pages;

/// <summary>A titled group of editable identity fields (Name, Job, Contact, Address, Settings).</summary>
public sealed record EditableSection(string Title, IReadOnlyList<EditableField> Fields);
