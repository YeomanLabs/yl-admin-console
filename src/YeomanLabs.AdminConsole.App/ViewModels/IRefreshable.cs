namespace YeomanLabs.AdminConsole.App.ViewModels;

/// <summary>Pages that can re-read their object from Graph on F5 (UX §11). Object pages implement this in M4.</summary>
public interface IRefreshable
{
    Task RefreshAsync(CancellationToken ct);
}
