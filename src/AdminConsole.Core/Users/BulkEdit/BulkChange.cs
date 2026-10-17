namespace AdminConsole.Core.Users.BulkEdit;

/// <summary>One field the admin ticked: the property and the value everyone gets. Null or empty clears it.</summary>
public sealed record BulkChange(string Property, string? Value);
