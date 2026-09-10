namespace AdminConsole.Core.Auth;

/// <summary>
/// Thrown by <see cref="IAuthService.GetTokenAsync"/> when no token can be obtained silently.
/// Callers on the UI path should catch it and call <see cref="IAuthService.EnsureSignedInAsync"/>;
/// background callers (sync) should surface it as "sign-in required" without popping a window.
/// </summary>
public sealed class SignInRequiredException : Exception
{
    public SignInRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}
