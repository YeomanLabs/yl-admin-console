namespace AdminConsole.Core.Cache;

public enum ActionOutcome
{
    Success,
    Failed,

    /// <summary>The action was evaluated and deliberately not performed (grey rule, already in target state, skip rule).</summary>
    Skipped,
}
