using System.Windows;
using Microsoft.Win32;

namespace AdminConsole.App.Services;

/// <summary>
/// The Windows save dialog, owned by the main window so it opens in front of the console rather than behind it (the M9
/// lesson: three "Choose…" buttons once opened behind their own dialog). View models ask through this, not a view.
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
}
