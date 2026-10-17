namespace AdminConsole.Core.OneDrive;

/// <summary>Where an export has got to (M16.4), for the status bar and the tab.</summary>
public sealed record ExportProgress(string Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string? Current = null)
{
    public string Text => FilesTotal == 0
        ? Phase
        : $"{Phase} {FilesDone} of {FilesTotal} files · {Megabytes(BytesDone)} of {Megabytes(BytesTotal)}";

    private static string Megabytes(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024.0:F0} KB" : $"{bytes / 1024.0 / 1024.0:F1} MB";
}
