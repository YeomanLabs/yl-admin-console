namespace AdminConsole.Core.OnPrem;

/// <summary>
/// What came back when the console asked AD for a machine's local administrator password (M12.6).
/// </summary>
/// <remarks>
/// Absent is the normal answer and has several different causes, all of which look identical from the outside: the
/// forest has no LAPS schema, LAPS is deployed but this machine has never checked in, or the admin has not been
/// delegated the right to read it. An admin standing in front of a machine that will not let them in needs to know
/// which, so each gets its own sentence rather than an empty box.
/// </remarks>
public sealed record LapsReadResult
{
    public LapsPassword? Password { get; init; }

    /// <summary>Why there is no password, in words. Null when there is one.</summary>
    public string? Reason { get; init; }

    public bool HasPassword => Password is not null;

    public static LapsReadResult Found(LapsPassword password) => new() { Password = password };

    public static LapsReadResult None(string reason) => new() { Reason = reason };

    public static LapsReadResult NoSchema => None(
        "This forest has no LAPS schema, so there is no password to read. Neither Windows LAPS nor the older " +
        "Microsoft LAPS has been installed here.");

    public static LapsReadResult NotSet => None(
        "LAPS is installed in this forest, but this machine has not stored a password. It has either never applied " +
        "the policy or has not checked in since it was applied.");

    public static LapsReadResult Encrypted => None(
        "The password is stored encrypted, which this console cannot decrypt. Read it with Get-LapsADPassword, " +
        "which can talk to the key-protection service.");
}
