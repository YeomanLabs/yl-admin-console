namespace AdminConsole.Core.Auth;

/// <summary>Interactive sign-in failed. <see cref="Kind"/> drives the UX §9 error state; <see cref="RawMessage"/> is the MSAL/AAD text for the Details expander.</summary>
public sealed class SignInException : Exception
{
    public SignInException(SignInFailureKind kind, string message, string? errorCode, string? rawMessage, Exception? inner)
        : base(message, inner)
    {
        Kind = kind;
        ErrorCode = errorCode;
        RawMessage = rawMessage;
    }

    public SignInFailureKind Kind { get; }
    public string? ErrorCode { get; }
    public string? RawMessage { get; }
}
