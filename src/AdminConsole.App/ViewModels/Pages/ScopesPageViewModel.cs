using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.App.Services;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>UX §9 step 2: our own page listing each requested read scope in plain English before the WAM prompt opens.</summary>
public sealed partial class ScopesPageViewModel : ObservableObject
{
    private readonly Func<Task> _onContinue;
    private readonly Action _onBack;

    public ScopesPageViewModel(Func<Task> onContinue, Action onBack)
    {
        _onContinue = onContinue;
        _onBack = onBack;
    }

    public IReadOnlyList<ScopeExplanation> Scopes { get; } = ScopeCatalog.ReadTier;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private bool _isBusy;

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private Task Continue() => _onContinue();

    private bool CanContinue() => !IsBusy;

    [RelayCommand]
    private void Back() => _onBack();
}
