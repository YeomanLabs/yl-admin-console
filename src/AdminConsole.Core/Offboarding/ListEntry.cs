using AdminConsole.Core.Models;

namespace AdminConsole.Core.Offboarding;

/// <summary>One pasted line and what it resolved to.</summary>
public sealed record ListEntry(string Input, ListMatch Match, DirectoryUser? User, IReadOnlyList<DirectoryUser> Candidates);
