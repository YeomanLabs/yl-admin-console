namespace AdminConsole.Core.OneDrive;

/// <summary>One file to export: Graph id, path inside the ZIP, size and OneDrive's hash.</summary>
public sealed record ExportItem(string Id, string Path, long Size, string? Hash, DateTimeOffset? Modified);
