using CommunityToolkit.Mvvm.Input;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>A button under a Mailbox tab section — "Add address…", "Add person…".</summary>
public sealed record SectionAction(string Label, IAsyncRelayCommand Command)
{
    public override string ToString() => Label;
}
