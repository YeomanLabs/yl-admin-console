using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using YeomanLabs.AdminConsole.Core.Graph;

namespace YeomanLabs.AdminConsole.App.ViewModels;

/// <summary>
/// Root of the UI. One instance for the process (the doc 03 "every VM transient" rule applies to page VMs; the shell
/// owns them). M3.1 scope: the non-modal error bar that the global exception handler targets.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ILogger<ShellViewModel> _logger;

    public ShellViewModel(ILogger<ShellViewModel> logger)
    {
        _logger = logger;
    }

    // ---- error bar (CLAUDE.md non-negotiable 2: human-readable message plus the raw error behind an expander) -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorTitle;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetails))]
    private string? _errorDetails;

    public bool HasError => ErrorTitle is not null;
    public bool HasErrorDetails => !string.IsNullOrEmpty(ErrorDetails);

    /// <summary>Shows the error bar. Safe from any thread; marshals to the UI thread.</summary>
    public void ShowError(string title, string message, string? details = null)
    {
        var app = System.Windows.Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => ShowError(title, message, details));
            return;
        }

        ErrorTitle = title;
        ErrorMessage = message;
        ErrorDetails = details;
    }

    /// <summary>Maps an exception to the bar: Graph errors get their code and request-id; everything else the type and message.</summary>
    public void ShowError(Exception ex, string? context = null)
    {
        var title = context ?? "Something went wrong";
        if (ex is GraphException g)
        {
            var details = $"HTTP {g.StatusCode} {g.ErrorCode}\nrequest-id: {g.RequestId ?? "n/a"}\nclient-request-id: {g.ClientRequestId ?? "n/a"}\n{g.Timestamp:u}\n\n{g.RawBody}";
            ShowError(title, g.Message, details);
            return;
        }

        ShowError(title, ex.Message, ex.ToString());
    }

    [RelayCommand]
    private void DismissError()
    {
        ErrorTitle = null;
        ErrorMessage = null;
        ErrorDetails = null;
    }

    [RelayCommand]
    private void CopyErrorDetails()
    {
        var text = $"{ErrorTitle}\n{ErrorMessage}\n\n{ErrorDetails}";
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard unavailable");
        }
    }
}
