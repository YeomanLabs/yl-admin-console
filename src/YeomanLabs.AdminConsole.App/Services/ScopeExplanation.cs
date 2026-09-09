namespace YeomanLabs.AdminConsole.App.Services;

/// <summary>One requested permission explained in plain English (UX §9 step 2, and the grey rule's "Needs the '{Scope}' permission").</summary>
public sealed record ScopeExplanation(string Scope, string Title, string Why);
