namespace AdminConsole.Core.Exchange;

/// <summary>One inbox rule, as offboarding needs it: enough to name it, say what it does to mail, and turn it off.</summary>
public sealed record InboxRuleRef
{
    /// <summary>The identity the rule cmdlets take: <c>{mailbox object id}\{RuleIdentity}</c> (proven 2026-10-17).</summary>
    public required string Identity { get; init; }

    public required string Name { get; init; }
    public bool Enabled { get; init; }

    /// <summary>Forwards, redirects or forwards as an attachment — the rules that send a leaver's mail somewhere else.</summary>
    public bool SendsMailElsewhere { get; init; }
}
