namespace AdminConsole.App.ViewModels.Palette;

/// <summary>A palette result group (Users, Devices, Groups, Actions). Empty groups are never created, so they collapse (UX §3).</summary>
public sealed class PaletteGroup
{
    public PaletteGroup(string title, IReadOnlyList<PaletteRow> rows)
    {
        Title = title;
        Rows = rows;
    }

    public string Title { get; }
    public IReadOnlyList<PaletteRow> Rows { get; }
}
