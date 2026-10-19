namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// A message to read and maybe copy, with one button. For text that carries something to pass on — an admin consent
/// link, Entra's reason — which a toast would take away before it could be read, and a confirm's text cannot be selected.
/// </summary>
public sealed class NoticeDialogViewModel : DialogViewModelBase
{
    public NoticeDialogViewModel(string title, string text)
    {
        Title = title;
        Text = text;
    }

    public override string Title { get; }

    public string Text { get; }

    public override string ConfirmText => "OK";

    public override bool ShowCancel => false;
}
