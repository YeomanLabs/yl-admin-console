using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Cache;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One line of the "Preparing your tenant…" list (UX §9 step 3): entity, ticking count, state.</summary>
public sealed partial class SyncProgressRow : ObservableObject
{
    public SyncProgressRow(CacheEntity? entity, string label)
    {
        Entity = entity;
        Label = label;
    }

    /// <summary>Null for the organization row, which is satisfied by the tenant-context load.</summary>
    public CacheEntity? Entity { get; }

    public string Label { get; }

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private int? _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private SyncRowState _state = SyncRowState.Pending;

    [ObservableProperty]
    private string? _error;

    public bool IsPending => State == SyncRowState.Pending;
    public bool IsRunning => State == SyncRowState.Running;
    public bool IsDone => State == SyncRowState.Done;
    public bool IsFailed => State == SyncRowState.Failed;

    public string CountText => Total is { } t ? $"{Count:N0} / {t:N0}" : Count > 0 ? Count.ToString("N0") : string.Empty;

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    partial void OnTotalChanged(int? value) => OnPropertyChanged(nameof(CountText));
}
