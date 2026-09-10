using FluentAssertions;
using Xunit;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>M12.3: the OU tree, which AD does not store — it is derived from distinguished names and nothing else.</summary>
public sealed class OuNodeTests
{
    private static OnPremOu Ou(string dn) => new()
    {
        ObjectGuid = Guid.NewGuid(),
        DistinguishedName = dn,
        Name = DistinguishedNames.LeafName(dn)!,
    };

    [Fact]
    public void ItNestsByDistinguishedName()
    {
        var roots = OuNode.Build(
        [
            Ou("OU=Users,OU=Lab,DC=adlab,DC=test"),
            Ou("OU=Lab,DC=adlab,DC=test"),
            Ou("OU=IT,OU=Users,OU=Lab,DC=adlab,DC=test"),
        ]);

        roots.Should().ContainSingle();
        var lab = roots[0];
        lab.Name.Should().Be("Lab");
        lab.Children.Should().ContainSingle(c => c.Name == "Users");
        lab.Children[0].Children.Should().ContainSingle(c => c.Name == "IT");
    }

    [Fact]
    public void DepthIsSetForIndentation()
    {
        var roots = OuNode.Build(
        [
            Ou("OU=Lab,DC=adlab,DC=test"),
            Ou("OU=Users,OU=Lab,DC=adlab,DC=test"),
            Ou("OU=IT,OU=Users,OU=Lab,DC=adlab,DC=test"),
        ]);

        roots[0].Flatten().Select(n => n.Depth).Should().Equal([0, 1, 2]);
    }

    [Fact]
    public void InputOrderDoesNotMatter()
    {
        var deepFirst = OuNode.Build(
        [
            Ou("OU=IT,OU=Users,OU=Lab,DC=adlab,DC=test"),
            Ou("OU=Users,OU=Lab,DC=adlab,DC=test"),
            Ou("OU=Lab,DC=adlab,DC=test"),
        ]);

        deepFirst.Should().ContainSingle("a child arriving before its parent must not become a second root");
        deepFirst[0].Flatten().Should().HaveCount(3);
    }

    [Fact]
    public void AnOuWhoseParentIsMissing_AttachesToTheNearestAncestorRatherThanVanishing()
    {
        // A search can miss a parent — a permission boundary, or a size limit. Dropping the child would silently
        // under-report what is in the directory, which is worse than showing it slightly out of place.
        var roots = OuNode.Build(
        [
            Ou("OU=Lab,DC=adlab,DC=test"),
            Ou("OU=IT,OU=Users,OU=Lab,DC=adlab,DC=test"),   // OU=Users is absent
        ]);

        roots.Should().ContainSingle();
        roots[0].Children.Should().ContainSingle(c => c.Name == "IT");
        roots[0].Children[0].Depth.Should().Be(1);
    }

    [Fact]
    public void SeparateBranchesEachBecomeARoot()
    {
        var roots = OuNode.Build([Ou("OU=Lab,DC=adlab,DC=test"), Ou("OU=Archive,DC=adlab,DC=test")]);

        roots.Should().HaveCount(2);
    }

    [Fact]
    public void NothingIn_NothingOut()
    {
        OuNode.Build([]).Should().BeEmpty();
    }
}
