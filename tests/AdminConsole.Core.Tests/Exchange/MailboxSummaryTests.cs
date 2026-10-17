using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Exchange;

public sealed class MailboxSummaryTests
{
    [Theory]
    [InlineData("UserMailbox", MailboxKind.User)]
    [InlineData("SharedMailbox", MailboxKind.Shared)]
    [InlineData("RoomMailbox", MailboxKind.Room)]
    [InlineData("EquipmentMailbox", MailboxKind.Equipment)]
    [InlineData("User", MailboxKind.None)]
    [InlineData("DisabledUser", MailboxKind.None)]
    [InlineData("MailUser", MailboxKind.MailUser)]
    [InlineData("GuestMailUser", MailboxKind.MailUser)]
    [InlineData("DiscoveryMailbox", MailboxKind.Other)]
    [InlineData("SomethingNewer", MailboxKind.Other)]
    [InlineData(null, MailboxKind.Unknown)]
    [InlineData("", MailboxKind.Unknown)]
    public void RecipientTypeDetails_MapToTheDistinctionsAnAdminActsOn(string? recipientType, MailboxKind kind)
    {
        MailboxKinds.From(recipientType).Should().Be(kind);
    }

    [Fact]
    public void SharedRoomAndEquipment_AreDisabledByDesign_AndAUserMailboxIsNot()
    {
        MailboxKinds.IsDisabledByDesign(MailboxKind.Shared).Should().BeTrue();
        MailboxKinds.IsDisabledByDesign(MailboxKind.Room).Should().BeTrue();
        MailboxKinds.IsDisabledByDesign(MailboxKind.User).Should().BeFalse();
        MailboxKinds.IsDisabledByDesign(MailboxKind.Unknown).Should().BeFalse();
    }

    [Fact]
    public void AnOrdinaryUserMailbox_CarriesNoBadge()
    {
        MailboxKinds.Badge(MailboxKind.User).Should().BeNull("every licensed person has one; a badge would be noise");
        MailboxKinds.Badge(MailboxKind.Unknown).Should().BeNull("unknown is not a fact to show");
        MailboxKinds.Badge(MailboxKind.Shared).Should().Be("Shared mailbox");
    }

    [Fact]
    public async Task Summaries_JoinGetUserAndGetMailbox_ByEntraObjectId()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/users"))
            .Enqueue(HttpStatusCode.OK, Fixtures.Text("exchange/mailboxes"));
        var sut = Service(handler);

        var summaries = await sut.GetMailboxSummariesAsync(CancellationToken.None);

        summaries.Should().HaveCount(15, "16 users less the discovery mailbox, which has no Entra object id to join to");
        summaries.Count(s => s.Kind == MailboxKind.Shared).Should().Be(3);
        summaries.Count(s => s.Kind == MailboxKind.None).Should().Be(5, "Get-User is the only walk that sees users with no mailbox");
        summaries.Count(s => s.Kind == MailboxKind.User).Should().Be(7);

        var shared = summaries.Single(s => s.UserId == "00000000-0000-0000-0000-000000000115");
        shared.PrimarySmtp.Should().Be("info@contoso.com");
        shared.HasMailbox.Should().BeTrue();

        handler.Requests[0].Body.Should().Contain("\"Get-User\"").And.Contain("\"ResultSize\":\"Unlimited\"");
        handler.Requests[0].Uri.Query.Should().Contain("RecipientTypeDetails");
        handler.Requests[1].Body.Should().Contain("\"Get-Mailbox\"");
    }

    [Fact]
    public void IsAvailable_OnlyOnceTheExchangeScopeIsGranted()
    {
        var tenant = new TenantContext();
        var sut = new ExchangeService(Client(new FakeHttpHandler(), tenant), tenant, NullLogger<ExchangeService>.Instance);

        sut.IsAvailable.Should().BeFalse("nothing is loaded");
        tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "a@contoso.com", GrantedScopes = [.. Scopes.Read] });
        sut.IsAvailable.Should().BeFalse("Graph scopes say nothing about Exchange");
        tenant.UpdateGrantedScopes([.. Scopes.Read, Scopes.ExchangeManage]);
        sut.IsAvailable.Should().BeTrue();
    }

    internal static ExchangeService Service(FakeHttpHandler handler, bool granted = true)
    {
        var tenant = new TenantContext();
        tenant.Update(new TenantContextSnapshot
        {
            TenantId = "t-1",
            SignedInUpn = "admin@contoso.com",
            GrantedScopes = granted ? [.. Scopes.Read, Scopes.ExchangeManage] : [.. Scopes.Read],
        });
        return new ExchangeService(Client(handler, tenant), tenant, NullLogger<ExchangeService>.Instance);
    }

    private static ExchangeClient Client(FakeHttpHandler handler, TenantContext tenant) => new(
        new HttpClient(handler), new FakeAuthService(), tenant, NullLogger<ExchangeClient>.Instance,
        new ExchangeClientOptions { BaseDelay = TimeSpan.Zero, Delay = (_, _) => Task.CompletedTask });
}
