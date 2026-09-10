using System.Runtime.InteropServices;
using System.Windows;

namespace AdminConsole.App.Services;

/// <summary>
/// Copying text without letting the clipboard take the app down. Another process can hold the clipboard open, and a
/// failed copy of a secret that is only shown once must not close the dialog showing it.
/// </summary>
public static class ClipboardText
{
    public static bool TryCopy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            Clipboard.SetDataObject(text, true);
            return true;
        }
        catch (ExternalException)
        {
            // COMException derives from this; both mean another process is holding the clipboard.
            return false;
        }
    }
}
