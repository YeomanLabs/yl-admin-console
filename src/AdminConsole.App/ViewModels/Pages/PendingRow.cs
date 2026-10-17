using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Templates;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One hybrid create on Home (M17.4): who, where it has got to, the last thing that happened, and Abandon.</summary>
public sealed partial class PendingRow
{
    private readonly Action<PendingRow> _abandon;

    public PendingRow(PendingProvisioning item, Action<PendingRow> abandon)
    {
        Item = item;
        _abandon = abandon;
    }

    public PendingProvisioning Item { get; }
    public string Title => $"{Item.DisplayName} · {Item.AdUserPrincipalName}";

    public string Detail
    {
        get
        {
            var age = DateTimeOffset.UtcNow - Item.CreatedAt;
            var since = age.TotalMinutes < 1 ? "just now" : age.TotalMinutes < 90 ? $"{age.TotalMinutes:F0} min ago" : $"{age.TotalHours:F1} h ago";
            return $"{Item.StateText} · created {since}" + (Item.TemplateName is { } t ? $" from {t}" : string.Empty);
        }
    }

    public string? LastNote => Item.Notes.LastOrDefault()?.Text;
    public bool HasProblems => Item.HasProblems;
    public bool CanAbandon => Item.IsOpen;
    public string AbandonName => $"Abandon the rest of {Item.DisplayName}'s setup";

    [RelayCommand]
    private void Abandon() => _abandon(this);
}
