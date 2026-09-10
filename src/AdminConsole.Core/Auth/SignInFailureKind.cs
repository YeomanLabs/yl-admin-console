namespace AdminConsole.Core.Auth;

/// <summary>The UX §9 sign-in failure states, mapped from MSAL so the App never has to know MSAL error codes.</summary>
public enum SignInFailureKind
{
    /// <summary>The user closed the prompt or picked no account → back to the sign-in page.</summary>
    Cancelled,

    /// <summary>Consent was declined → explain the app cannot work without read access; offer retry.</summary>
    ConsentDeclined,

    /// <summary>Blocked by Conditional Access → show the message verbatim and "Contact your Entra admin".</summary>
    ConditionalAccess,

    /// <summary>Anything else (network, broker, unexpected service error).</summary>
    Other,
}
