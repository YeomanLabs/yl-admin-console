using YeomanLabs.AdminConsole.Core.Graph;

namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>
/// One section of a live hydration: either a value or the Graph error that section hit. Sections fail independently
/// (a 403 on sign-in activity must not blank the manager), so tabs can degrade one at a time (UX §4, §6).
/// </summary>
public sealed record SectionResult<T>
{
    private SectionResult(T? value, GraphException? error, bool loaded)
    {
        Value = value;
        Error = error;
        IsLoaded = loaded;
    }

    public T? Value { get; }
    public GraphException? Error { get; }

    /// <summary>False until the batch returned; the UI shows a skeleton while false.</summary>
    public bool IsLoaded { get; }

    public bool IsSuccess => IsLoaded && Error is null;
    public bool IsForbidden => Error?.StatusCode == 403;

    public static SectionResult<T> Pending { get; } = new(default, null, loaded: false);
    public static SectionResult<T> Ok(T value) => new(value, null, loaded: true);
    public static SectionResult<T> Failed(GraphException error) => new(default, error, loaded: true);
}
