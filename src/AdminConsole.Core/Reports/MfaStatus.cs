namespace AdminConsole.Core.Reports;

/// <summary>Where one person stands on MFA (M19.1).</summary>
public enum MfaStatus
{
    /// <summary>An app, a passkey, a security key, Windows Hello or an OTP token.</summary>
    Ready,

    /// <summary>Registered, but only by phone (text or call) — the weakest second factor there is.</summary>
    PhoneOnly,

    /// <summary>Nothing that counts as a second factor.</summary>
    Missing,

    /// <summary>Not expected to register: sign-in blocked, a guest (MFA happens at home), or a shared or resource mailbox.</summary>
    Exempt,
}
