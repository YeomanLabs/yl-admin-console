using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Search;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// Offboard a list (M15.5): paste names or sign-in names, see who each one is, settle every line that is not exactly
/// one person, then offboard them one at a time — each with its own preview, in the ordinary offboard dialog.
/// </summary>
public sealed partial class OffboardListDialogViewModel : DialogViewModelBase
{
    private readonly CacheDb _db;
    private readonly SearchService _search;

    public OffboardListDialogViewModel(CacheDb db, SearchService search)
    {
        _db = db;
        _search = search;
    }

    public override string Title => "Offboard a list";

    public override double Width => 720;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand))]
    private string _text = string.Empty;

    public ObservableCollection<OffboardListRow> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>The people to offboard, in the order they were pasted.</summary>
    public IReadOnlyList<DirectoryUser> Chosen => [.. Rows.Select(r => r.Chosen).OfType<DirectoryUser>()];

    public int Unresolved => Rows.Count(r => !r.IsResolved);

    public override bool CanConfirm => HasRows && Unresolved == 0 && Chosen.Count > 0;

    public override string ConfirmText => Chosen.Count == 1 ? "Offboard 1 person" : $"Offboard {Chosen.Count} people";

    public override bool IsDestructive => true;

    public string? StatusText => !HasRows ? null
        : Unresolved > 0 ? $"{Unresolved} line{(Unresolved == 1 ? " needs" : "s need")} a choice before anything can run."
        : Chosen.Count == 0 ? "Every line is skipped."
        : $"Each person gets their own preview, one after another. Closing a preview stops the list there.";

    private bool CanFind() => Text.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanFind))]
    private void Find()
    {
        Rows.Clear();
        foreach (var entry in OffboardList.Resolve(OffboardList.Parse(Text), _db, _search)) Rows.Add(new OffboardListRow(entry, Changed));
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(Chosen));
        OnPropertyChanged(nameof(Unresolved));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(StatusText));
    }
}
