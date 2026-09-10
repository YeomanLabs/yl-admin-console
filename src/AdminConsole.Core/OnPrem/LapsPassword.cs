namespace AdminConsole.Core.OnPrem;

/// <summary>The local administrator password AD is holding for a machine (M12.6).</summary>
public sealed record LapsPassword
{
    public required string Password { get; init; }

    /// <summary>The local account the password belongs to. Windows LAPS records it; legacy LAPS does not.</summary>
    public string? AccountName { get; init; }

    /// <summary>When AD will rotate it. A date in the past means the machine has not checked in.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public required LapsSource Source { get; init; }
}

/// <summary>Which LAPS wrote the password, since the two store it in entirely different attributes.</summary>
public enum LapsSource
{
    /// <summary>Windows LAPS, built into Windows since 2023. Stores JSON in <c>msLAPS-Password</c>.</summary>
    Windows,

    /// <summary>The original downloadable LAPS. Stores plain text in <c>ms-Mcs-AdmPwd</c>.</summary>
    Legacy,
}
