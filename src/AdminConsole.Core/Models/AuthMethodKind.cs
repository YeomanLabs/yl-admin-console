namespace AdminConsole.Core.Models;

/// <summary>The polymorphic <c>@odata.type</c> values of <c>/users/{id}/authentication/methods</c> (doc 04 §Users).</summary>
public enum AuthMethodKind
{
    Unknown,
    Password,
    MicrosoftAuthenticator,
    Phone,
    Fido2,
    WindowsHelloForBusiness,
    Email,
    SoftwareOath,
    TemporaryAccessPass,
}
