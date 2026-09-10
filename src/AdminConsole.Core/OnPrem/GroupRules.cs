namespace AdminConsole.Core.OnPrem;

/// <summary>
/// What Active Directory will and will not allow to a group (M12.5), decided before the write rather than after it.
/// </summary>
/// <remarks>
/// <para>
/// Group scope is the part of AD with the most rules and the least helpful errors. Converting a global group to a
/// domain local one is not a thing you can do; converting it to universal depends on what the group is a member of;
/// converting universal back to global depends on what the group contains. Attempted blind, each of those comes back
/// as <c>UnwillingToPerform</c> — "the server is unwilling to perform" — which tells an admin nothing about which of
/// the three situations they are in.
/// </para>
/// <para>
/// So the rules live here as sentences. Every one of them is a real constraint the domain controller enforces; this
/// is not the console inventing policy, it is the console explaining policy that already exists. Where AD has the
/// final say — a constraint only it can see — the write still goes ahead and its refusal is translated.
/// </para>
/// <para>
/// A pre-check that guessed wrong in the strict direction would be worse than no pre-check at all: it would block
/// work the directory permits, and the admin would have no way to tell whose rule stopped them. So everything
/// refused here was confirmed refused by a real domain controller, and everything that controller permits is
/// permitted here — nesting loops included, which it turns out AD does not mind in the slightest.
/// </para>
/// </remarks>
public static class GroupRules
{
    /// <summary>
    /// Why this scope change cannot be made, or null when it can.
    /// </summary>
    /// <param name="from">The scope now.</param>
    /// <param name="to">The scope wanted.</param>
    /// <param name="isMemberOfGlobalGroup">Whether the group is itself a member of a global group.</param>
    /// <param name="containsDomainLocalGroup">Whether a domain local group is one of its members.</param>
    /// <param name="containsUniversalGroup">Whether a universal group is one of its members.</param>
    public static string? ScopeChange(
        GroupScope from,
        GroupScope to,
        bool isMemberOfGlobalGroup,
        bool containsDomainLocalGroup,
        bool containsUniversalGroup)
    {
        if (from == to) return null;

        return (from, to) switch
        {
            // The two direct conversions AD has never allowed. Universal is the way across, and saying so turns a
            // dead end into an instruction.
            (GroupScope.Global, GroupScope.DomainLocal) =>
                "Active Directory cannot convert a global group straight to domain local. Convert it to universal first, then to domain local.",
            (GroupScope.DomainLocal, GroupScope.Global) =>
                "Active Directory cannot convert a domain local group straight to global. Convert it to universal first, then to global.",

            // A global group may only be a member of another global group, so a group on its way to universal has to
            // leave those memberships behind first.
            (GroupScope.Global, GroupScope.Universal) when isMemberOfGlobalGroup =>
                "This group is a member of a global group, and a universal group cannot be. Remove it from that group first.",

            // A domain local group may only contain domain local groups from its own domain; universal groups may not.
            (GroupScope.DomainLocal, GroupScope.Universal) when containsDomainLocalGroup =>
                "This group contains a domain local group, which a universal group cannot. Remove it first.",

            // A global group may only contain global groups.
            (GroupScope.Universal, GroupScope.Global) when containsUniversalGroup =>
                "This group contains a universal group, which a global group cannot. Remove it first.",

            _ => null,
        };
    }

    /// <summary>
    /// Why <paramref name="memberScope"/> cannot go into a group of <paramref name="groupScope"/>, or null when it can.
    /// </summary>
    /// <param name="groupScope">The scope of the group being added to.</param>
    /// <param name="memberKind">What kind of object is being added.</param>
    /// <param name="memberScope">The member's own scope, when the member is a group.</param>
    /// <remarks>
    /// Single-domain rules. A forest with more than one domain adds "from this domain only" clauses that the console
    /// cannot check without a global catalogue query, and getting those wrong in the strict direction would block
    /// legitimate work — so cross-domain membership is left for the domain controller to rule on.
    /// </remarks>
    public static string? Nesting(GroupScope groupScope, OnPremMemberKind memberKind, GroupScope? memberScope)
    {
        if (memberKind != OnPremMemberKind.Group || memberScope is not { } inner) return null;

        return (groupScope, inner) switch
        {
            (GroupScope.Global, GroupScope.DomainLocal) or (GroupScope.Global, GroupScope.Universal) =>
                $"A global group can only contain global groups. {Describe(inner)} cannot go in one.",
            (GroupScope.Universal, GroupScope.DomainLocal) =>
                "A universal group cannot contain a domain local group. Domain local groups sit at the end of the chain, not in the middle.",
            _ => null,
        };
    }

    /// <summary>Why this group's scope or category cannot be changed at all, or null when it can.</summary>
    /// <remarks>
    /// Domain Users, Domain Admins and the rest are flagged <c>isCriticalSystemObject</c>. AD refuses to convert
    /// them, and it is better to say which group and why than to send the request and translate the refusal.
    /// </remarks>
    public static string? Protected(OnPremGroup group) =>
        group.IsCritical
            ? $"{group.Primary} is a built-in group. Active Directory does not allow its scope or category to be changed."
            : null;

    /// <summary>
    /// What changing the category will actually do, when it is worth saying out loud before it happens. Null when
    /// there is nothing to warn about.
    /// </summary>
    /// <remarks>
    /// A distribution group carries no rights at all. Converting a security group to one does not warn, does not
    /// fail, and quietly takes away every permission that was granted through it — the sort of change that is
    /// noticed days later by whoever lost access.
    /// </remarks>
    public static string? CategoryWarning(GroupCategory from, GroupCategory to, int memberCount)
    {
        if (from != GroupCategory.Security || to != GroupCategory.Distribution) return null;

        var who = memberCount == 1 ? "its one member" : $"all {memberCount} members";
        return $"A distribution group grants no access. Any permission given through this group stops working for {who}.";
    }

    /// <summary>
    /// What a nesting loop would mean, when adding this member would make one. Null when it would not.
    /// </summary>
    /// <remarks>
    /// A warning and not a refusal, because the domain controller allows it. Tried against a real DC, every loop was
    /// accepted without complaint — a group inside itself, a two-group loop, a three-group loop. The widely repeated
    /// claim that AD prevents circular nesting is not what AD does.
    ///
    /// So refusing here would make the console stricter than the directory it is a window onto, which is the one
    /// thing a pre-check must never be: it would block work that is permitted, and an admin would have no way to
    /// tell whether the rule was AD's or ours. Saying what the change means and letting it happen is the honest
    /// position. Nothing breaks — token evaluation does not follow loops — but the nesting stops carrying meaning,
    /// and it is almost always a mis-click rather than an intention.
    /// </remarks>
    public static string? CycleWarning(OnPremGroup group, Guid memberGuid, string memberDn)
    {
        if (group.ObjectGuid == memberGuid)
        {
            return $"{group.Primary} would be a member of itself. Active Directory allows this and nothing breaks, but it means nothing.";
        }

        if (!group.MemberOfDns.Any(dn => string.Equals(dn, memberDn, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var other = DistinguishedNames.LeafName(memberDn) ?? "that group";
        return $"{other} already contains {group.Primary}, so this makes a loop. Active Directory allows it and nothing breaks, but the nesting stops meaning anything.";
    }

    private static string Describe(GroupScope scope) => scope switch
    {
        GroupScope.DomainLocal => "A domain local group",
        GroupScope.Universal => "A universal group",
        _ => "A global group",
    };
}
