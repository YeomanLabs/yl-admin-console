namespace AdminConsole.Core.Exchange;

/// <summary>
/// <c>RecipientTypeDetails</c> → <see cref="MailboxKind"/>, and the words for each. One place, because the cache filters,
/// the palette badges and the Mailbox tab must all agree on what "shared" and "no mailbox" mean.
/// </summary>
public static class MailboxKinds
{
    public const string UserMailbox = "UserMailbox";
    public const string SharedMailbox = "SharedMailbox";
    public const string RoomMailbox = "RoomMailbox";
    public const string EquipmentMailbox = "EquipmentMailbox";

    public static MailboxKind From(string? recipientTypeDetails) => recipientTypeDetails switch
    {
        null or "" => MailboxKind.Unknown,
        UserMailbox => MailboxKind.User,
        SharedMailbox => MailboxKind.Shared,
        RoomMailbox => MailboxKind.Room,
        EquipmentMailbox => MailboxKind.Equipment,
        "User" or "DisabledUser" or "GuestUser" => MailboxKind.None,
        "MailUser" or "GuestMailUser" or "RemoteUserMailbox" or "RemoteSharedMailbox" or "RemoteRoomMailbox" or "RemoteEquipmentMailbox"
            => MailboxKind.MailUser,
        _ => MailboxKind.Other,
    };

    /// <summary>A short badge, or null where a badge would only be noise (an ordinary user mailbox, or not known).</summary>
    public static string? Badge(MailboxKind kind) => kind switch
    {
        MailboxKind.Shared => "Shared mailbox",
        MailboxKind.Room => "Room",
        MailboxKind.Equipment => "Equipment",
        MailboxKind.None => "No mailbox",
        MailboxKind.MailUser => "Mailbox elsewhere",
        _ => null,
    };

    /// <summary>
    /// Shared, room and equipment mailboxes are disabled accounts by design. Badging them "Disabled" reads as a fault
    /// and sends an admin looking for a leaver who does not exist.
    /// </summary>
    public static bool IsDisabledByDesign(MailboxKind kind) => kind is MailboxKind.Shared or MailboxKind.Room or MailboxKind.Equipment;

    public static string Describe(MailboxKind kind) => kind switch
    {
        MailboxKind.User => "User mailbox",
        MailboxKind.Shared => "Shared mailbox",
        MailboxKind.Room => "Room mailbox",
        MailboxKind.Equipment => "Equipment mailbox",
        MailboxKind.None => "No mailbox in this tenant",
        MailboxKind.MailUser => "Mail user — the mailbox lives elsewhere (on-prem, or another tenant)",
        MailboxKind.Other => "System mailbox",
        _ => "Not read from Exchange yet",
    };
}
