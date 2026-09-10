namespace AdminConsole.Core.OnPrem;

/// <summary>
/// The result of a write to Active Directory (M12.1), shaped like <c>WriteOutcome</c> so the UI treats an on-prem
/// change and a Graph change the same way.
/// </summary>
public sealed record DirectoryOutcome
{
    public required bool Success { get; init; }

    /// <summary>What happened, in the past tense, for a toast and the action log.</summary>
    public required string Summary { get; init; }

    /// <summary>Set when it failed: a sentence the admin can act on, from <see cref="DirectoryErrorText"/>.</summary>
    public string? FriendlyError { get; init; }

    /// <summary>The LDAP result code, for the details expander. Never the whole story on its own.</summary>
    public int? ResultCode { get; init; }

    /// <summary>The server's diagnostic text, behind the expander.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>True when nothing needed doing — already in the wanted state.</summary>
    public bool WasNoOp { get; init; }

    /// <summary>
    /// Set on a write that succeeded but did something worth saying out loud (M12.5) — nesting a group into a loop,
    /// which AD permits without complaint.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="FriendlyError"/> because it is not a failure and must not read as one. The console
    /// refuses only what the directory refuses; where the directory allows something surprising, the console does it
    /// and says what it did.
    /// </remarks>
    public string? Warning { get; init; }

    /// <summary>
    /// Set when the change was made on-prem and the cloud copy has not caught up. The UI shows this rather than
    /// letting an admin wonder whether it worked (doc 01: "never wonder did that sync yet").
    /// </summary>
    public bool AwaitingSync { get; init; }

    public static DirectoryOutcome Ok(string summary, bool awaitingSync = true, string? warning = null) =>
        new() { Success = true, Summary = summary, AwaitingSync = awaitingSync, Warning = warning };

    public static DirectoryOutcome NoOp(string summary) =>
        new() { Success = true, Summary = summary, WasNoOp = true, AwaitingSync = false };

    public static DirectoryOutcome Failed(string summary, int resultCode, string? diagnostic, string? what = null) => new()
    {
        Success = false,
        Summary = summary,
        ResultCode = resultCode,
        Diagnostic = diagnostic,
        FriendlyError = DirectoryErrorText.Friendly(resultCode, diagnostic, what),
    };

    public static DirectoryOutcome Refused(string summary, string reason) =>
        new() { Success = false, Summary = summary, FriendlyError = reason };
}
