using AdminConsole.Core.Cache.Browse;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>A group as one row of the Groups list.</summary>
public sealed class GroupRow(GroupListRow row)
{
    public GroupListRow Model { get; } = row;

    public string Id => Model.Id;
    public string Primary => Model.Primary;
    public string? Mail => Model.Mail;
    public string? Description => Model.Description;
    public bool Dynamic => Model.Dynamic;
    public bool Synced => Model.Synced;
    public bool RoleAssignable => Model.RoleAssignable;

    public string MemberCountText => Model.MemberCount switch
    {
        0 => "No members",
        1 => "1 member",
        var n => $"{n:N0} members",
    };

    /// <summary>Graph's derived kind in the words the portal uses.</summary>
    public string KindText => Model.Kind switch
    {
        "M365" => "Microsoft 365",
        "DistributionOrMailSecurity" => "Distribution",
        var k => k,
    };
}
