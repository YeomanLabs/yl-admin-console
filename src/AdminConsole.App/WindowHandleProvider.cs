namespace AdminConsole.App;

/// <summary>
/// The main window's HWND for the MSAL WAM broker (CLAUDE.md: the broker needs a parent window handle). Set once from
/// <c>SourceInitialized</c> in App.xaml.cs so the auth service can read it from any thread without touching WPF.
/// </summary>
public sealed class WindowHandleProvider
{
    private IntPtr _handle;

    public IntPtr Handle
    {
        get => Interlocked.CompareExchange(ref _handle, IntPtr.Zero, IntPtr.Zero);
        set => Interlocked.Exchange(ref _handle, value);
    }
}
