using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;

namespace YeomanLabs.AdminConsole.Core.Tests.Auth;

public sealed class TenantContextLoaderTests
{
    private const string BatchBody = """
        {"responses":[
          {"id":"org","status":200,"body":{"value":[{
            "id":"c570b99a-c52d-47f4-997e-59c3b3205cf6",
            "displayName":"YeomanLabs Consulting",
            "countryLetterCode":"US",
            "onPremisesSyncEnabled":true,
            "onPremisesLastSyncDateTime":"2026-09-09T05:00:00Z",
            "verifiedDomains":[{"name":"yeomanlab.onmicrosoft.com"},{"name":"yeomanlabs.co"}],
            "assignedPlans":[
              {"servicePlanId":"41781fb2-bc02-4b7c-bd55-b576c07bb09d","capabilityStatus":"Enabled","service":"AADPremiumService"},
              {"servicePlanId":"eec0eb4f-6444-4f95-aba0-50c24d67f998","capabilityStatus":"Deleted","service":"AADPremiumService"}
            ]}]}},
          {"id":"me","status":200,"body":{"id":"u-1","displayName":"Andrew","userPrincipalName":"andrew@yeomanlabs.co"}},
          {"id":"roles","status":200,"body":{"value":[
            {"displayName":null,"roleTemplateId":"62e90394-69f5-4237-9190-012177145e10"},
            {"displayName":null,"roleTemplateId":"3a2c62db-5318-420d-8d74-23affee5d9d5"},
            {"displayName":"Custom Role","roleTemplateId":"11111111-2222-3333-4444-555555555555"}
          ]}}
        ]}
        """;

    [Fact]
    public async Task Load_PopulatesContextFromOneBatch()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, BatchBody);
        var context = new TenantContext();
        var changed = 0;
        context.Changed += (_, _) => changed++;

        var result = await CreateLoader(handler, context).LoadAsync(CancellationToken.None);

        result.Should().BeSameAs(context);
        context.IsLoaded.Should().BeTrue();
        changed.Should().Be(1);
        context.TenantId.Should().Be("c570b99a-c52d-47f4-997e-59c3b3205cf6");
        context.TenantDisplayName.Should().Be("YeomanLabs Consulting");
        context.VerifiedDomains.Should().Equal("yeomanlab.onmicrosoft.com", "yeomanlabs.co");
        context.CountryLetterCode.Should().Be("US");
        context.OnPremisesSyncEnabled.Should().BeTrue();
        context.OnPremisesLastSyncDateTime.Should().Be(new DateTimeOffset(2026, 9, 9, 5, 0, 0, TimeSpan.Zero));
        context.SignedInUserId.Should().Be("u-1");
        context.SignedInUpn.Should().Be("andrew@yeomanlabs.co");
        context.SignedInDisplayName.Should().Be("Andrew");
        context.HasP1.Should().BeTrue();
        context.HasP2.Should().BeFalse("the P2 plan is Deleted, not Enabled");
        context.Roles.Should().HaveCount(3);
        context.Roles.Select(r => r.DisplayName).Should().Equal("Global Administrator", "Intune Administrator", "Custom Role",
            "Graph blanks displayName without Directory.Read.All, so known template IDs map to local names");
        context.IsGlobalAdmin.Should().BeTrue();
        context.HasRole(DirectoryRoleRef.IntuneAdministrator).Should().BeTrue();
        context.HasRole(DirectoryRoleRef.UserAdministrator).Should().BeFalse();
        context.GrantedScopes.Should().BeEquivalentTo(Scopes.Read);
        context.HasScope("user.read.all").Should().BeTrue();

        handler.Requests.Should().ContainSingle().Which.Uri.AbsolutePath.Should().Be("/v1.0/$batch");
        using var sent = JsonDocument.Parse(handler.Requests[0].Body!);
        var urls = sent.RootElement.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("url").GetString()).ToList();
        urls.Should().HaveCount(3);
        urls[0].Should().StartWith("/organization?$select=id,displayName,verifiedDomains");
        urls[1].Should().Be("/me?$select=id,displayName,userPrincipalName");
        urls[2].Should().Be("/me/memberOf/microsoft.graph.directoryRole?$select=displayName,roleTemplateId");
    }

    [Fact]
    public async Task Load_RolesForbidden_StillSucceedsWithNoRoles()
    {
        const string body = """
            {"responses":[
              {"id":"org","status":200,"body":{"value":[{"id":"t","displayName":"T","assignedPlans":[]}]}},
              {"id":"me","status":200,"body":{"id":"u","displayName":"U","userPrincipalName":"u@t"}},
              {"id":"roles","status":403,"body":{"error":{"code":"Authorization_RequestDenied","message":"nope"}}}
            ]}
            """;
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, body);
        var context = new TenantContext();

        await CreateLoader(handler, context).LoadAsync(CancellationToken.None);

        context.IsLoaded.Should().BeTrue();
        context.Roles.Should().BeEmpty();
        context.HasP1.Should().BeFalse();
        context.OnPremisesSyncEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Load_OrganizationForbidden_Throws()
    {
        const string body = """
            {"responses":[
              {"id":"org","status":403,"body":{"error":{"code":"Authorization_RequestDenied","message":"nope"}}},
              {"id":"me","status":200,"body":{"id":"u"}},
              {"id":"roles","status":200,"body":{"value":[]}}
            ]}
            """;
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, body);
        var context = new TenantContext();

        var act = () => CreateLoader(handler, context).LoadAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<GraphException>()).Which.ErrorCode.Should().Be("Authorization_RequestDenied");
        context.IsLoaded.Should().BeFalse();
    }

    private static TenantContextLoader CreateLoader(FakeHttpHandler handler, TenantContext context)
    {
        var graph = new GraphClient(new HttpClient(handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        return new TenantContextLoader(graph, new FakeAuthService(), context, NullLogger<TenantContextLoader>.Instance);
    }
}
