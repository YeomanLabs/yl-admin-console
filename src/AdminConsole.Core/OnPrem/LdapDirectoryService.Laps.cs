using System.DirectoryServices.Protocols;
using System.Text.Json;

namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Reading a machine's local administrator password out of AD (M12.6).
/// </summary>
/// <remarks>
/// Two products, two schemas, two encodings. Windows LAPS — in the box since 2023 — writes a JSON document into
/// <c>msLAPS-Password</c>, or an encrypted blob into <c>msLAPS-EncryptedPassword</c> when encryption is switched on.
/// The older downloadable LAPS writes plain text into <c>ms-Mcs-AdmPwd</c>. A forest can have either, both or
/// neither, and the attribute simply not existing is the commonest case of all.
///
/// The one thing this must never do is answer "no password" to all of those. An admin standing in front of a machine
/// that will not let them in is trying to work out which situation they are in, and "nothing here" is the same
/// answer for a forest without LAPS, a machine that has never checked in, and a delegation they have not been given.
/// </remarks>
public sealed partial class LdapDirectoryService
{
    private const string WindowsLapsPassword = "msLAPS-Password";
    private const string WindowsLapsEncrypted = "msLAPS-EncryptedPassword";
    private const string WindowsLapsExpiry = "msLAPS-PasswordExpirationTime";
    private const string LegacyLapsPassword = "ms-Mcs-AdmPwd";
    private const string LegacyLapsExpiry = "ms-Mcs-AdmPwdExpirationTime";

    public async Task<LapsReadResult> GetLapsPasswordAsync(Guid computerGuid, CancellationToken ct)
    {
        var dn = await ResolveDnAsync(computerGuid, ct).ConfigureAwait(false);
        if (dn is null) return LapsReadResult.None("That computer no longer exists in Active Directory.");

        // Asking for an attribute the schema does not have is not an error — it simply comes back absent — so both
        // schemas can be asked for at once and the answer worked out from what arrives.
        var entry = await ReadByDnAsync(dn,
            [WindowsLapsPassword, WindowsLapsEncrypted, WindowsLapsExpiry, LegacyLapsPassword, LegacyLapsExpiry], ct)
            .ConfigureAwait(false);

        if (entry is null) return LapsReadResult.None("That computer could not be read from Active Directory.");

        if (LdapAttributes.String(entry, WindowsLapsPassword) is { Length: > 0 } json)
        {
            return ParseWindowsLaps(json, LdapAttributes.FileTime(entry, WindowsLapsExpiry));
        }

        if (entry.Attributes.Contains(WindowsLapsEncrypted)) return LapsReadResult.Encrypted;

        if (LdapAttributes.String(entry, LegacyLapsPassword) is { Length: > 0 } legacy)
        {
            return LapsReadResult.Found(new LapsPassword
            {
                Password = legacy,
                // The original LAPS only ever managed the built-in administrator, and does not record which account
                // it was. Naming it would be a guess; saying nothing is accurate.
                AccountName = null,
                ExpiresAt = LdapAttributes.FileTime(entry, LegacyLapsExpiry),
                Source = LapsSource.Legacy,
            });
        }

        // Nothing came back. Whether that is "no schema" or "no value" is the difference between installing LAPS and
        // waiting for a machine to check in, so it is worth one more query to tell them apart.
        var known = await GetWritableAttributesAsync(computerGuid, ct).ConfigureAwait(false);
        if (known is null) return LapsReadResult.NotSet;

        var hasSchema = known.Contains(WindowsLapsPassword) || known.Contains(LegacyLapsPassword)
            || known.Contains(WindowsLapsEncrypted);

        return hasSchema ? LapsReadResult.NotSet : LapsReadResult.NoSchema;
    }

    /// <summary>
    /// Windows LAPS stores a small JSON document rather than a bare string: <c>{"n":"Administrator","t":"...","p":"..."}</c>.
    /// </summary>
    private static LapsReadResult ParseWindowsLaps(string json, DateTimeOffset? expiry)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var password = root.TryGetProperty("p", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(password)) return LapsReadResult.NotSet;

            return LapsReadResult.Found(new LapsPassword
            {
                Password = password,
                AccountName = root.TryGetProperty("n", out var n) ? n.GetString() : null,
                ExpiresAt = expiry,
                Source = LapsSource.Windows,
            });
        }
        catch (JsonException)
        {
            // Not the shape this version of Windows LAPS is documented to write. Handing back the raw value would
            // put whatever it is on screen and possibly into a clipboard; saying so is safer and more honest.
            return LapsReadResult.None(
                "Active Directory returned a LAPS value this console does not recognise. Read it with Get-LapsADPassword.");
        }
    }
}
