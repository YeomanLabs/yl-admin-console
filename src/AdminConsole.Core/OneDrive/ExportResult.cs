namespace AdminConsole.Core.OneDrive;

/// <summary>How an export ended (M16.4).</summary>
public sealed record ExportResult
{
    public required string ZipPath { get; init; }
    public int Files { get; init; }
    public long Bytes { get; init; }

    /// <summary>Files already downloaded by an earlier, interrupted run and checked again rather than fetched again.</summary>
    public int Resumed { get; init; }

    /// <summary>Files whose content matched OneDrive's own hash.</summary>
    public int Verified { get; init; }

    /// <summary>Files OneDrive gave no hash for (rare: some very old items), so only their size was checked.</summary>
    public int SizeOnly { get; init; }

    public IReadOnlyList<string> Failed { get; init; } = [];

    public bool Complete => Failed.Count == 0;

    public string Summary => Complete
        ? $"Exported {Files} file{(Files == 1 ? string.Empty : "s")} ({Bytes / 1024.0 / 1024.0:F1} MB); {Verified} checked against OneDrive's hash" +
          (SizeOnly > 0 ? $", {SizeOnly} by size only" : string.Empty)
        : $"Exported {Files - Failed.Count} of {Files} files; {Failed.Count} could not be fetched intact";
}
