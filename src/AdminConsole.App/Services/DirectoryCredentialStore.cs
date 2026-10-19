using System.Net;
using System.Windows;
using Microsoft.Extensions.Logging;
using AdminConsole.App.ViewModels.Dialogs;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.App.Services;

/// <summary>
/// The passwords of the accounts on-prem domains sign in as (Settings → Active Directory → Connect as), for this run of
/// the console only: asked for once, held in memory, never written or logged, gone when the console closes. Shared by
/// every tenant's directory service.
/// </summary>
/// <remarks>
/// The dialog service shows one dialog at a time and opening a second cancels the first, so this never asks while
/// another dialog is open — an offboarding preview reaching AD must not close the offboarding. It answers "not signed
/// in" instead, and the Domain page's Sign in asks again.
/// </remarks>
public sealed class DirectoryCredentialStore : IDirectoryCredentials
{
    private readonly DialogService _dialogs;
    private readonly ILogger<DirectoryCredentialStore> _logger;
    private readonly Dictionary<string, NetworkCredential> _remembered = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _declined = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _asking = new(1, 1);

    public DirectoryCredentialStore(DialogService dialogs, ILogger<DirectoryCredentialStore> logger)
    {
        _dialogs = dialogs;
        _logger = logger;
    }

    private static string Key(string domain, string account) => $"{domain.Trim()}|{account.Trim()}";

    /// <summary>A credential typed elsewhere (the connect dialog), so reconnecting does not ask for it again.</summary>
    public void Remember(string domain, string account, NetworkCredential credential)
    {
        lock (_remembered)
        {
            _remembered[Key(domain, account)] = credential;
            _declined.Remove(Key(domain, account));
        }
    }

    public void Forget(string domain, string account)
    {
        lock (_remembered) _remembered.Remove(Key(domain, account));
    }

    public async Task<NetworkCredential?> GetAsync(string domain, string account, bool askAgain, CancellationToken ct)
    {
        var key = Key(domain, account);
        lock (_remembered)
        {
            if (_remembered.TryGetValue(key, out var known)) return known;
            if (_declined.Contains(key) && !askAgain) return null;
        }

        if (Application.Current?.Dispatcher is not { } ui) return null;

        // One question at a time: several AD calls at once must not stack several prompts.
        await _asking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_remembered)
            {
                if (_remembered.TryGetValue(key, out var known)) return known;
            }

            var answer = await ui.InvokeAsync(async () =>
            {
                if (_dialogs.IsOpen) return (NetworkCredential?)null;
                var dialog = new AdSignInDialogViewModel(domain, account);
                if (!await _dialogs.ShowAsync(dialog))
                {
                    lock (_remembered) _declined.Add(key);
                    _logger.LogInformation("Sign-in to {Domain} declined", domain);
                    return null;
                }

                return DirectoryAccount.ToCredential(account, dialog.Password);
            }).Task.Unwrap().ConfigureAwait(false);

            if (answer is not null)
            {
                lock (_remembered)
                {
                    _remembered[key] = answer;
                    _declined.Remove(key);
                }
            }

            return answer;
        }
        finally
        {
            _asking.Release();
        }
    }
}
