namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Turns an LDAP failure into a sentence an admin can act on (M12.1), the way <c>GraphErrorText</c> does for Graph.
/// </summary>
/// <remarks>
/// This matters more on-prem than it does in the cloud. AD has no scope list to check in advance, so unlike Graph the
/// console often cannot know whether a write is permitted until it tries — which makes the message after the failure
/// the whole of the explanation. "The server is unwilling to perform" is not an explanation.
///
/// The useful detail is usually not the LDAP result code but the Win32 code AD hides in the diagnostic message, as
/// eight hex digits: <c>data 525</c>, <c>data 52e</c>, <c>data 532</c>. Those distinguish "no such user" from "wrong
/// password" from "password expired", and they are what makes the difference between a message that helps and one
/// that does not.
/// </remarks>
public static class DirectoryErrorText
{
    // LDAP result codes, RFC 4511 §A.
    public const int Success = 0;
    public const int OperationsError = 1;
    public const int NoSuchObject = 32;
    public const int InvalidDnSyntax = 34;
    public const int EntryAlreadyExists = 68;
    public const int NotAllowedOnNonLeaf = 66;
    public const int ObjectClassViolation = 65;
    public const int ConstraintViolation = 19;
    public const int AttributeOrValueExists = 20;
    public const int NoSuchAttribute = 16;
    public const int InsufficientAccessRights = 50;
    public const int UnwillingToPerform = 53;
    public const int InvalidCredentials = 49;
    public const int Busy = 51;
    public const int Unavailable = 52;
    public const int SizeLimitExceeded = 4;
    public const int ReferralCode = 10;
    public const int StrongAuthRequired = 8;

    /// <summary>
    /// What to show the admin. <paramref name="diagnostic"/> is the server's extended message, which is where the
    /// Win32 sub-code lives and where the real reason usually is.
    /// </summary>
    public static string Friendly(int resultCode, string? diagnostic = null, string? what = null)
    {
        var subject = string.IsNullOrWhiteSpace(what) ? "that object" : what;

        // The sub-code is more specific than the result code, so it wins where there is one.
        var sub = Win32SubCode(diagnostic);
        if (sub is not null)
        {
            var bySub = FromSubCode(sub);
            if (bySub is not null) return bySub;
        }

        return resultCode switch
        {
            Success => "Done.",
            NoSuchObject => $"{Capitalise(subject)} no longer exists in Active Directory. It may have been moved or deleted since this page was loaded.",
            EntryAlreadyExists => "Something with that name already exists in that container. Names have to be unique within an OU.",
            InvalidDnSyntax => "That distinguished name is not valid. This is a bug in the console rather than something you did.",
            NotAllowedOnNonLeaf => "That container still holds objects. Empty it, or move what is in it, before deleting it.",
            ObjectClassViolation => "Active Directory rejected the object as incomplete — a required attribute is missing.",
            AttributeOrValueExists => "That value is already set. Nothing needed changing.",
            NoSuchAttribute => "That value was not there to remove.",
            ConstraintViolation => "Active Directory rejected the value. It may be too long, the wrong format, or already in use.",
            InsufficientAccessRights => $"Your account has not been delegated permission to do that to {subject}. Rights in AD are granted per container, so having them elsewhere does not help here.",
            InvalidCredentials => "Active Directory refused the sign-in. Your Windows session may have expired — sign out and back in.",
            StrongAuthRequired => "This operation needs an encrypted connection. Setting a password requires LDAPS on 636 with a certificate this machine trusts, or a signed and sealed connection.",
            Busy or Unavailable => "The domain controller is busy or unavailable. Try again, or check whether it is reachable.",
            SizeLimitExceeded => "The domain controller returned only part of the results because there were too many. Narrow the search.",
            ReferralCode => "That object lives in another domain. Cross-domain operations are not supported yet.",
            UnwillingToPerform => "The domain controller refused the operation. This usually means a policy on the domain forbids it.",
            OperationsError => "The domain controller reported an internal error handling the request.",
            _ => $"Active Directory returned error {resultCode}." + (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $" {Trim(diagnostic)}"),
        };
    }

    /// <summary>
    /// The Win32 code AD embeds in a diagnostic message as <c>data 52e</c>. Returned lowercase without padding.
    /// </summary>
    public static string? Win32SubCode(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic)) return null;

        var marker = diagnostic.IndexOf("data ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0 || IsZeroAt(diagnostic, marker + 5))
        {
            // A password refusal arrives as "0000052D: SvcErr: ... data 0" — the code that explains it is the eight
            // hex digits at the front, and the "data" field is zero. Reading only after "data " gets nothing useful
            // and falls back to "the server refused", which tells an admin resetting a password precisely nothing.
            return LeadingCode(diagnostic);
        }

        var start = marker + 5;
        var end = start;
        while (end < diagnostic.Length && Uri.IsHexDigit(diagnostic[end])) end++;
        if (end == start) return null;

        return diagnostic[start..end].TrimStart('0').ToLowerInvariant() is { Length: > 0 } trimmed
            ? trimmed
            : "0";
    }

    /// <summary>The eight hex digits AD puts before the first colon, e.g. <c>0000052D</c>.</summary>
    private static string? LeadingCode(string diagnostic)
    {
        var colon = diagnostic.IndexOf(':');
        if (colon <= 0) return null;

        var head = diagnostic[..colon].Trim();
        if (head.Length is < 4 or > 8 || !head.All(Uri.IsHexDigit)) return null;

        var trimmed = head.TrimStart('0').ToLowerInvariant();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool IsZeroAt(string diagnostic, int index)
    {
        var end = index;
        while (end < diagnostic.Length && Uri.IsHexDigit(diagnostic[end])) end++;
        return end > index && diagnostic[index..end].TrimStart('0').Length == 0;
    }

    private static string? FromSubCode(string sub) => sub switch
    {
        "525" => "There is no account with that name.",
        "52e" => "The password is wrong.",
        "530" => "That account is not allowed to sign in at this time of day.",
        "531" => "That account is not allowed to sign in from this computer.",
        "532" => "The password has expired.",
        "533" => "That account is disabled.",
        "534" => "That account is not allowed that type of sign-in.",
        "701" => "That account has expired.",
        "773" => "The password must be changed before the account can be used.",
        "775" => "That account is locked out. Unlock it before trying again.",
        // Password-set refusals, which are the ones an admin most often meets.
        "52d" => "The password does not meet the domain's policy — usually its length, complexity, or how recently it was last changed.",
        "56" => "The new password matches one the domain remembers. The password history policy forbids reuse.",
        _ => null,
    };

    private static string Trim(string diagnostic)
    {
        // AD appends its own reference noise after the useful sentence.
        var cut = diagnostic.IndexOf(", data ", StringComparison.OrdinalIgnoreCase);
        var text = cut > 0 ? diagnostic[..cut] : diagnostic;
        return text.Trim().TrimEnd('.') + ".";
    }

    private static string Capitalise(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
