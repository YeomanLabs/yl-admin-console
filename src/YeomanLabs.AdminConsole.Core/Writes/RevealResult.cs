using YeomanLabs.AdminConsole.Core.Graph;

namespace YeomanLabs.AdminConsole.Core.Writes;

/// <summary>
/// The result of revealing a secret (UX §5 Security). Deliberately separate from <see cref="WriteOutcome"/>, whose
/// <c>Summary</c> feeds toasts and the action log: a secret must never be able to travel that way.
/// </summary>
public sealed record RevealResult<T>
{
    public required bool Success { get; init; }

    /// <summary>The revealed value. Held in memory for the dialog only; never cached and never logged.</summary>
    public T? Value { get; init; }

    /// <summary>UX §7 text when the reveal failed or there was nothing to show.</summary>
    public string? FriendlyError { get; init; }

    public GraphException? Error { get; init; }

    public string? Details => Error is null ? null : GraphErrorText.Details(Error);

    public static RevealResult<T> Ok(T value) => new() { Success = true, Value = value };

    public static RevealResult<T> Fail(string friendlyError, GraphException? error) =>
        new() { Success = false, FriendlyError = friendlyError, Error = error };
}
