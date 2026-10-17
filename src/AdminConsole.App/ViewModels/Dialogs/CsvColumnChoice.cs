namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>One column of an imported CSV whose header the console did not recognise, labelled with what is in it.</summary>
public sealed record CsvColumnChoice(int Index, string Label)
{
    public override string ToString() => Label;
}
