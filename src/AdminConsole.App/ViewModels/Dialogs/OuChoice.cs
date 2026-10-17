namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>Where a restore puts the AD account back (M15.4). A null DN means "where the snapshot found it".</summary>
public sealed record OuChoice(string? Dn, string Label)
{
    public override string ToString() => Label;
}
