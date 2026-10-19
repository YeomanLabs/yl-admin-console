using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace AdminConsole.App.Services;

/// <summary>
/// The Windows open and save dialogs, owned by the main window so they open in front of the console rather than behind
/// it (the M9 lesson: three "Choose…" buttons once opened behind their own dialog). View models ask through this, not a
/// view.
/// </summary>
public sealed class FilePicker
{
    /// <summary>A path to save to, or null when the admin cancels.</summary>
    public string? SaveAs(string suggestedName, string filter, string title)
    {
        var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = filter,
            Title = title,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    /// <summary>An existing file to read, or null when the admin cancels.</summary>
    public string? OpenFile(string filter, string title)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            Title = title,
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    /// <summary>Existing files to read, or none when the admin cancels.</summary>
    public IReadOnlyList<string> OpenFiles(string filter, string title)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            Title = title,
            CheckFileExists = true,
            Multiselect = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileNames : [];
    }

    /// <summary>An existing folder, or null when the admin cancels.</summary>
    public string? PickFolder(string title, string? initialFolder = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
            InitialDirectory = initialFolder is { Length: > 0 } && Directory.Exists(initialFolder) ? initialFolder : string.Empty,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
    }
}
