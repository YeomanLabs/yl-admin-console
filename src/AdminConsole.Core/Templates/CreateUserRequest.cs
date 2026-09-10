namespace AdminConsole.Core.Templates;

/// <summary>
/// Everything the create flow needs (doc 07 §Create-from-template flow step 2). The form fills the person-specific
/// parts; the rest comes from the template and may have been edited before it got here.
/// </summary>
public sealed record CreateUserRequest
{
    public required string DisplayName { get; init; }
    public required string GivenName { get; init; }
    public required string Surname { get; init; }
    public required string UserPrincipalName { get; init; }
    public required string MailNickname { get; init; }

    /// <summary>The template's attribute set, already merged with anything the admin edited on the form.</summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();

    public string? ManagerId { get; init; }
    public IReadOnlyList<string> SkuIds { get; init; } = [];
    public IReadOnlyList<TemplateGroup> Groups { get; init; } = [];

    public required string Password { get; init; }
    public bool ForceChangePasswordNextSignIn { get; init; } = true;

    /// <summary>Doc 07: usage location has to be set at creation or the licence step fails.</summary>
    public string? UsageLocation => Attributes.TryGetValue("usageLocation", out var v) ? v as string : null;
}

/// <summary>One step of a create (doc 07 §flow step 4). Simpler than an offboarding step: nothing here is undone.</summary>
public sealed record CreateStepResult(string Title, StepState State, string Message)
{
    public Graph.GraphException? Error { get; init; }

    public string? Details => Error is null ? null : Graph.GraphErrorText.Details(Error);

    public static CreateStepResult Ok(string title, string message) => new(title, StepState.Success, message);
    public static CreateStepResult Skip(string title, string message) => new(title, StepState.Skipped, message);
    public static CreateStepResult Fail(string title, string message, Graph.GraphException? error = null) =>
        new(title, StepState.Failed, message) { Error = error };
}

/// <summary>How one create step went.</summary>
public enum StepState
{
    Success,
    Failed,
    Skipped,
    NotRun,
}

/// <summary>
/// The outcome of a create (doc 07 §flow step 5). The password lives here and nowhere else: not in the cache, not in
/// the action log, and not on any step message.
/// </summary>
public sealed record CreateUserResult
{
    public required bool Success { get; init; }
    public string? UserId { get; init; }
    public string? UserPrincipalName { get; init; }

    /// <summary>Shown once on the result screen. Null when the account was never created.</summary>
    public string? Password { get; init; }

    public required IReadOnlyList<CreateStepResult> Steps { get; init; }
    public TimeSpan Elapsed { get; init; }

    public int Completed => Steps.Count(s => s.State == StepState.Success);
    public int Failed => Steps.Count(s => s.State == StepState.Failed);

    /// <summary>"4 completed · 1 failed · 3 s", with the zero counts left out.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(2);
            if (Completed > 0) parts.Add($"{Completed} completed");
            if (Failed > 0) parts.Add($"{Failed} failed");
            if (parts.Count == 0) parts.Add("nothing to do");
            return $"{string.Join(" · ", parts)} · {Elapsed.TotalSeconds:F0} s";
        }
    }
}
