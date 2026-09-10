using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Sync;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// UX §9 step 3: "Preparing your tenant…" with counts ticking up. Raises <see cref="UsersReady"/> as soon as the users
/// walk finishes so the shell can land on Home while groups/devices continue in the background.
/// </summary>
public sealed partial class SyncProgressPageViewModel : ObservableObject, IDisposable
{
    private readonly ISyncStatus _sync;
    private bool _usersReadyRaised;

    public SyncProgressPageViewModel(ISyncStatus sync)
    {
        _sync = sync;
        Rows =
        [
            new SyncProgressRow(null, "Organization") { State = SyncRowState.Done },
            new SyncProgressRow(CacheEntity.Users, "Users"),
            new SyncProgressRow(CacheEntity.Groups, "Groups"),
            new SyncProgressRow(CacheEntity.Devices, "Entra devices"),
            new SyncProgressRow(CacheEntity.ManagedDevices, "Intune devices"),
        ];

        _sync.ProgressChanged += OnProgress;
        foreach (var latest in _sync.Latest.Values) Apply(latest);
    }

    public IReadOnlyList<SyncProgressRow> Rows { get; }

    [ObservableProperty]
    private string _headline = "Preparing your tenant…";

    [ObservableProperty]
    private string _detail = "Building the local index. The palette opens as soon as users are in; the rest continues in the background.";

    public event EventHandler? UsersReady;

    private void OnProgress(object? sender, SyncProgress p)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Apply(p));
            return;
        }

        Apply(p);
    }

    private void Apply(SyncProgress p)
    {
        var row = Rows.FirstOrDefault(r => r.Entity == p.Entity);
        if (row is null) return;

        row.Count = p.Processed;
        row.Total = p.Total;
        if (p.Error is not null)
        {
            row.State = SyncRowState.Failed;
            row.Error = p.Error.Message;
        }
        else
        {
            row.State = p.IsDone ? SyncRowState.Done : SyncRowState.Running;
        }

        if (p.Entity == CacheEntity.Users && p.IsDone && p.Error is null && !_usersReadyRaised)
        {
            _usersReadyRaised = true;
            UsersReady?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => _sync.ProgressChanged -= OnProgress;
}
