using AdminConsole.Core.Reports;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One person in a subscription's list of holders (0.3.3): who, how they hold it, and when they last signed in.</summary>
public sealed record LicenceHolderRow(LicensedUserRow User, string SkuId)
{
    public string HowHeld => User.HowHeld(SkuId) switch
    {
        "Group" => "Through a group",
        "Direct and group" => "Directly and through a group",
        _ => "Directly",
    };

    public bool IsBlocked => !User.Enabled;

    public override string ToString() => $"{User.DisplayName}, {HowHeld}, {User.Reason}";

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so after a rebuild a row equal
    // to the one before took over that row's detached peer and dropped out of what a screen reader can reach (0.3.3).
    public bool Equals(LicenceHolderRow? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
