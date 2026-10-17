namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One column a list can export (M18.5). <see cref="OnRow"/> marks the ones the row already draws; the others, when
/// chosen, appear on the row as an extra line, so what is chosen is what is on screen is what is exported.
/// </summary>
public sealed record ListColumn<TRow>(string Key, string Label, Func<TRow, string?> Value, bool DefaultOn = true, bool OnRow = true);
