namespace AdminConsole.Core.Exchange;

/// <summary>
/// One section of the Mailbox tab: a value, or the Exchange error that section hit. Sections load and fail
/// independently, the way User 360's Graph sections do — a 403 on folder permissions must not blank the mailbox size.
/// </summary>
public sealed record ExchangeSection<T>
{
    private ExchangeSection(T? value, ExchangeException? error, bool loaded)
    {
        Value = value;
        Error = error;
        IsLoaded = loaded;
    }

    public T? Value { get; }
    public ExchangeException? Error { get; }

    /// <summary>False until the call returned; the UI shows a skeleton while false.</summary>
    public bool IsLoaded { get; }

    public bool IsSuccess => IsLoaded && Error is null;
    public bool IsForbidden => Error?.IsForbidden == true;
    public bool IsNotFound => Error?.IsNotFound == true;

    /// <summary>The sentence an admin reads when the section failed.</summary>
    public string? Message => Error is null ? null : ExchangeErrorText.Friendly(Error);

    public static ExchangeSection<T> Pending { get; } = new(default, null, loaded: false);
    public static ExchangeSection<T> Ok(T value) => new(value, null, loaded: true);
    public static ExchangeSection<T> Failed(ExchangeException error) => new(default, error, loaded: true);
}
