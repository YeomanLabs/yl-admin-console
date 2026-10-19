using FluentAssertions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;

namespace AdminConsole.Core.Tests.Offboarding;

/// <summary>An offboarding template's automatic reply: one text for every leaver, filled in per run.</summary>
public sealed class AutoReplyTextTests
{
    private static readonly AutoReplyText.Values Adele = new()
    {
        Name = "Adele Vance",
        FirstName = "Adele",
        Email = "adele@contoso.com",
        Delegate = "Megan Bowen",
        DelegateEmail = "megan@contoso.com",
        Company = "Contoso",
    };

    [Fact]
    public void Render_FillsEveryPlaceholder_WhateverItsCase()
    {
        var text = AutoReplyText.Render("{FirstName} ({EMAIL}) has left {company}. Please write to {delegate} at {delegateEmail}. — {name}", Adele);

        text.Should().Be("Adele (adele@contoso.com) has left Contoso. Please write to Megan Bowen at megan@contoso.com. — Adele Vance");
    }

    [Fact]
    public void Render_LeavesAnythingElseInBracesAsWritten_SoThePreviewShowsIt()
    {
        AutoReplyText.Render("Ticket {ref} for {name}", Adele).Should().Be("Ticket {ref} for Adele Vance");
    }

    [Fact]
    public void Render_UsesTheDelegatesNameWhenItHasNoAddress()
    {
        AutoReplyText.Render("Write to {delegateEmail}", Adele with { DelegateEmail = null }).Should().Be("Write to Megan Bowen");
    }

    [Fact]
    public void UnknownTokens_NamesEachOnceInTheOrderWritten()
    {
        AutoReplyText.UnknownTokens("{manager} and {Name} and {phone} and {MANAGER}").Should().Equal("{manager}", "{phone}");
        AutoReplyText.UnknownTokens("No placeholders at all, {} aside").Should().Equal("{}");
        AutoReplyText.UnknownTokens(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Contact {delegate}.", true)]
    [InlineData("Contact {DelegateEmail}.", true)]
    [InlineData("{name} has left {company}.", false)]
    [InlineData(null, false)]
    public void NeedsDelegate_IsTrueOnlyWhenTheTextNamesTheDelegate(string? text, bool expected)
    {
        AutoReplyText.NeedsDelegate(text).Should().Be(expected);
    }

    [Fact]
    public void For_TakesTheLeaverTheDelegateAndTheTenantFromTheRun()
    {
        var tenant = new TenantContext();
        tenant.Update(new TenantContextSnapshot { TenantId = "t-1", TenantDisplayName = "Contoso Ltd" });
        var ctx = Context(tenant, new DirectoryUser { Id = "u-1", AccountEnabled = true, OnPremisesSyncEnabled = false, Licensed = true, DisplayName = "Adele Vance", GivenName = "Adele", Mail = "adele@contoso.com", UserPrincipalName = "av@contoso.onmicrosoft.com", Raw = "{}" },
            new PersonRef("u-2", "Megan Bowen", "megan@contoso.com"));

        var values = AutoReplyText.For(ctx);

        values.Should().BeEquivalentTo(new AutoReplyText.Values
        {
            Name = "Adele Vance", FirstName = "Adele", Email = "adele@contoso.com",
            Delegate = "Megan Bowen", DelegateEmail = "megan@contoso.com", Company = "Contoso Ltd",
        });
    }

    [Fact]
    public void For_FallsBackWhenTheDirectoryHasLess()
    {
        // No first name, no mail, no tenant name yet: the reply still reads as a sentence.
        var ctx = Context(new TenantContext(), new DirectoryUser { Id = "u-1", AccountEnabled = true, OnPremisesSyncEnabled = false, Licensed = true, DisplayName = "Adele Vance", UserPrincipalName = "adele@contoso.com", Raw = "{}" }, null);

        var values = AutoReplyText.For(ctx);

        values.FirstName.Should().Be("Adele");
        values.Email.Should().Be("adele@contoso.com");
        values.Company.Should().Be("our organisation");
        values.Delegate.Should().BeNull();
    }

    private static OffboardingContext Context(TenantContext tenant, DirectoryUser user, PersonRef? delegateTo) => new()
    {
        User = user,
        Tenant = tenant,
        Delegate = delegateTo,
        CorrelationId = "run-1",
    };
}
