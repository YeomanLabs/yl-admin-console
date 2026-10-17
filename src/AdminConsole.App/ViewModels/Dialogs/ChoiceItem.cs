namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>One option in a dialog's drop-down: the value sent to the service, and the words the admin reads.</summary>
public sealed record ChoiceItem(string Value, string Label)
{
    public override string ToString() => Label;
}
