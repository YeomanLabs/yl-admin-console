namespace AdminConsole.Core.OnPrem;

/// <summary>How an AD create went (M17.2), and the account when it exists.</summary>
public sealed record CreatedAdUser(DirectoryOutcome Outcome, OnPremUser? User);
