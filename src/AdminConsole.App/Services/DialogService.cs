using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.App.ViewModels.Dialogs;

namespace AdminConsole.App.Services;

/// <summary>
/// Modal dialogs, MVVM-style: a page view-model builds a <see cref="DialogViewModelBase"/> and awaits the answer; the
/// shell's overlay renders whatever is current. Page view-models never touch a Window.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows the dialog and completes with true only if the admin confirmed it.</summary>
    Task<bool> ShowAsync(DialogViewModelBase dialog);
}

/// <summary>
/// The one dialog at a time the shell can show. Opening a second closes the first as cancelled, so a stray double
/// click can never leave an orphaned dialog awaiting an answer nobody can give.
/// </summary>
public sealed partial class DialogService : ObservableObject, IDialogService
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private DialogViewModelBase? _current;

    public bool IsOpen => Current is not null;

    public async Task<bool> ShowAsync(DialogViewModelBase dialog)
    {
        Current?.CancelCommand.Execute(null);
        Current = dialog;
        try
        {
            // The caller is on the UI thread and its continuation has to stay there, so no ConfigureAwait(false).
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog)) Current = null;
        }
    }
}
