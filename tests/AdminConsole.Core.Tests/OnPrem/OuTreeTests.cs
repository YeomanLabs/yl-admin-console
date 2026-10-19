using FluentAssertions;
using Xunit;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Tests.OnPrem;

/// <summary>The Domain page's OU tree: collapsed to start, opened a branch at a time.</summary>
public sealed class OuTreeTests
{
    private static OnPremOu Ou(string dn) => new() { ObjectGuid = Guid.NewGuid(), DistinguishedName = dn, Name = dn.Split(',')[0][3..] };

    private static IReadOnlyList<OuNode> Tree() => OuNode.Build(
    [
        Ou("OU=Company,DC=corp,DC=com"),
        Ou("OU=Sales,OU=Company,DC=corp,DC=com"),
        Ou("OU=East,OU=Sales,OU=Company,DC=corp,DC=com"),
        Ou("OU=Finance,OU=Company,DC=corp,DC=com"),
        Ou("OU=Servers,DC=corp,DC=com"),
    ]);

    [Fact]
    public void ATreeStartsCollapsed_ShowingOnlyItsTopLevel()
    {
        OuNode.Visible(Tree()).Select(n => n.Name).Should().Equal("Company", "Servers");
    }

    [Fact]
    public void OpeningABranch_ShowsItsChildren_ButNotTheirs()
    {
        var roots = Tree();
        roots[0].IsExpanded = true;

        OuNode.Visible(roots).Select(n => n.Name).Should().Equal("Company", "Finance", "Sales", "Servers");
    }

    [Fact]
    public void ClosingAParent_HidesEverythingBelowIt_AndKeepsWhatWasOpenForNextTime()
    {
        var roots = Tree();
        roots[0].IsExpanded = true;
        roots[0].Children.Single(c => c.Name == "Sales").IsExpanded = true;
        OuNode.Visible(roots).Select(n => n.Name).Should().Equal("Company", "Finance", "Sales", "East", "Servers");

        roots[0].IsExpanded = false;
        OuNode.Visible(roots).Select(n => n.Name).Should().Equal("Company", "Servers");

        roots[0].IsExpanded = true;
        OuNode.Visible(roots).Should().Contain(n => n.Name == "East", "Sales was left open");
    }

    [Fact]
    public void TheToggle_SaysWhatItWillDo_AndAnnouncesTheChange()
    {
        var node = Tree()[0];
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        node.ToggleLabel.Should().Be("Expand Company");
        node.IsExpanded = true;

        node.ToggleLabel.Should().Be("Collapse Company");
        changed.Should().Contain([nameof(OuNode.IsExpanded), nameof(OuNode.ToggleLabel)]);
        node.HasChildren.Should().BeTrue();
        Tree()[1].HasChildren.Should().BeFalse("Servers has no OUs beneath it");
    }
}
