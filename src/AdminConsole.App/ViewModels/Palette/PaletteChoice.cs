using AdminConsole.Core.Models;

namespace AdminConsole.App.ViewModels.Palette;

/// <summary>What the admin picked in the palette. <see cref="NewTab"/> is Ctrl+Enter (UX §3).</summary>
public sealed record PaletteChoice(SearchHit Hit, bool NewTab);
