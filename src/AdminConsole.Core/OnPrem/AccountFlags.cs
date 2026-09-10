namespace AdminConsole.Core.OnPrem;

/// <summary>
/// The <c>userAccountControl</c> bit field (M12.1).
/// </summary>
/// <remarks>
/// This exists so nothing else touches those bits by hand. Enabling an account is clearing one bit of sixteen, and a
/// read-modify-write that assigns instead of masking silently discards the rest — turning off "password never
/// expires" and "smartcard required" on the way past. It is the classic way to break an account while appearing to
/// do something small, so every change goes through here and every case has a test.
/// </remarks>
public static class AccountFlags
{
    public const int Script = 0x0001;
    public const int AccountDisabled = 0x0002;
    public const int HomeDirRequired = 0x0008;
    public const int Lockout = 0x0010;
    public const int PasswordNotRequired = 0x0020;
    public const int PasswordCantChange = 0x0040;
    public const int EncryptedTextPasswordAllowed = 0x0080;
    public const int NormalAccount = 0x0200;
    public const int WorkstationTrustAccount = 0x1000;
    public const int ServerTrustAccount = 0x2000;
    public const int DontExpirePassword = 0x10000;
    public const int SmartcardRequired = 0x40000;
    public const int TrustedForDelegation = 0x80000;
    public const int NotDelegated = 0x100000;
    public const int UseDesKeyOnly = 0x200000;
    public const int DontRequirePreauth = 0x400000;
    public const int PasswordExpired = 0x800000;
    public const int TrustedToAuthForDelegation = 0x1000000;

    /// <summary>What a newly created, enabled user should have.</summary>
    public const int NewEnabledUser = NormalAccount;

    /// <summary>What a newly created user has before its password is set — AD refuses an enabled account without one.</summary>
    public const int NewDisabledUser = NormalAccount | AccountDisabled;

    public static bool IsDisabled(int value) => (value & AccountDisabled) != 0;

    public static bool PasswordNeverExpires(int value) => (value & DontExpirePassword) != 0;

    public static bool SmartcardIsRequired(int value) => (value & SmartcardRequired) != 0;

    public static bool IsComputer(int value) =>
        (value & WorkstationTrustAccount) != 0 || (value & ServerTrustAccount) != 0;

    /// <summary>Enables or disables, leaving every other bit exactly as it was.</summary>
    public static int SetEnabled(int value, bool enabled) =>
        enabled ? value & ~AccountDisabled : value | AccountDisabled;

    public static int SetPasswordNeverExpires(int value, bool never) =>
        never ? value | DontExpirePassword : value & ~DontExpirePassword;

    /// <summary>The flags that are set, in words, for a details panel or a log line.</summary>
    public static IReadOnlyList<string> Describe(int value)
    {
        var names = new List<string>();
        void Add(int flag, string text)
        {
            if ((value & flag) != 0) names.Add(text);
        }

        Add(AccountDisabled, "Disabled");
        Add(Lockout, "Locked out");
        Add(DontExpirePassword, "Password never expires");
        Add(PasswordNotRequired, "Password not required");
        Add(PasswordCantChange, "Cannot change password");
        Add(SmartcardRequired, "Smartcard required");
        Add(TrustedForDelegation, "Trusted for delegation");
        Add(NotDelegated, "Account is sensitive and cannot be delegated");
        Add(DontRequirePreauth, "Kerberos pre-authentication not required");
        Add(UseDesKeyOnly, "DES encryption only");
        Add(PasswordExpired, "Password expired");
        return names;
    }
}
