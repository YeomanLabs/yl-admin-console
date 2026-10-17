using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Pim;
using AdminConsole.Core.Tests.Fakes;

namespace AdminConsole.Core.Tests.Auth;

/// <summary>M18.2: my eligible roles, their policies, self-activation, and a tenant without PIM.</summary>
public sealed class PimServiceTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string AuthAdmin = DirectoryRoleRef.AuthenticationAdministrator;
    private const string UserAdmin = DirectoryRoleRef.UserAdministrator;
    private readonly FakeHttpHandler _http = new();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly PimService _sut;

    public PimServiceTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com", SignedInUserId = "me-1", HasP2 = true, GrantedScopes = [.. Scopes.Write] });
        var graph = new GraphClient(new HttpClient(_http), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new PimService(graph, _tenant, _actions, NullLogger<PimService>.Instance);
    }

    public void Dispose() => _actions.Dispose();

    private static string Instance(string role, string name, string? end = null, string? type = null, string member = "Direct") =>
        "{\"roleDefinitionId\":\"" + role + "\",\"memberType\":\"" + member + "\",\"endDateTime\":" + (end is null ? "null" : "\"" + end + "\"") + "," +
        (type is null ? string.Empty : "\"assignmentType\":\"" + type + "\",") +
        "\"roleDefinition\":{\"id\":\"" + role + "\",\"displayName\":\"" + name + "\",\"templateId\":\"" + role + "\"}}";

    [Fact]
    public async Task GetMyRoles_MergesEligibleWithActive_AndTellsTheGreyRuleWhatIsEligible()
    {
        _http.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{Instance(AuthAdmin, "Authentication Administrator")}},{{Instance(UserAdmin, "User Administrator", member: "Group")}}]}""")
            .Enqueue(HttpStatusCode.OK, $$"""{"value":[{{Instance(UserAdmin, "User Administrator", "2026-10-17T18:00:00Z", "Activated")}}]}""");

        var read = await _sut.GetMyRolesAsync(Ct);

        read.IsAvailable.Should().BeTrue();
        var auth = read.Roles.Single(r => r.RoleTemplateId == AuthAdmin);
        auth.IsActive.Should().BeFalse();
        var user = read.Roles.Single(r => r.RoleTemplateId == UserAdmin);
        user.IsActive.Should().BeTrue();
        user.ViaGroup.Should().BeTrue();
        user.ActiveUntil.Should().Be(DateTimeOffset.Parse("2026-10-17T18:00:00Z"));

        _http.Requests[0].Uri.ToString().Should().Contain("filterByCurrentUser(on='principal')", "only my own roles");
        _tenant.EligibleRoles.Select(r => r.RoleTemplateId).Should().Equal([AuthAdmin]);
        _tenant.HasRole(UserAdmin).Should().BeTrue("an active activation counts as holding the role");

        var check = new PermissionChecker(_tenant).Check(Capabilities.IssueTap);
        check.EligibleRole!.RoleTemplateId.Should().Be(AuthAdmin);
        check.Reason.Should().Contain("eligible for Authentication Administrator");
    }

    [Fact]
    public async Task GetMyRoles_WithoutAP2Licence_SaysTheTenantHasNoPim_NotThatYouHaveNoRoles()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUserId = "me-1", HasP2 = false });
        _http.Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":"AadPremiumLicenseRequired","message":"The tenant needs an AAD Premium 2 license."}}""");

        var read = await _sut.GetMyRolesAsync(Ct);

        read.IsAvailable.Should().BeFalse();
        read.Unavailable.Should().Be(PimService.NoLicence);
    }

    [Fact]
    public void ParsePolicy_ReadsMaximumAndWhatActivationDemands()
    {
        using var doc = JsonDocument.Parse("""
            [{"id":"Expiration_EndUser_Assignment","maximumDuration":"PT4H","isExpirationRequired":true},
             {"id":"Enablement_EndUser_Assignment","enabledRules":["Justification","Ticketing","MultiFactorAuthentication"]},
             {"id":"Approval_EndUser_Assignment","setting":{"isApprovalRequired":false}}]
            """);

        var policy = PimService.ParsePolicy(doc.RootElement);

        policy.MaximumDuration.Should().Be(TimeSpan.FromHours(4));
        policy.RequiresJustification.Should().BeTrue();
        policy.RequiresTicket.Should().BeTrue();
        policy.RequiresMfa.Should().BeTrue();
        policy.RequiresApproval.Should().BeFalse();
        policy.Durations.Should().Equal([TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4)], "never more than the policy allows");
    }

    [Fact]
    public async Task GetPolicy_Unreadable_FallsBackToDefaults_MarkedAssumed()
    {
        _http.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"no"}}""");

        var policy = await _sut.GetPolicyAsync(AuthAdmin, Ct);

        policy.IsAssumed.Should().BeTrue();
        policy.MaximumDuration.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public async Task Activate_SendsSelfActivateForMe_WithDurationJustificationAndTicket()
    {
        _http.Enqueue(HttpStatusCode.Created, """{"id":"req-1","status":"Provisioned"}""");
        var role = new PimRole { RoleDefinitionId = AuthAdmin, RoleTemplateId = AuthAdmin, DisplayName = "Authentication Administrator" };

        var outcome = await _sut.ActivateAsync(new PimActivation { Role = role, Duration = TimeSpan.FromHours(2), Justification = " Ticket work ", TicketNumber = "INC-9", TicketSystem = "ServiceNow" }, Ct);

        outcome.Success.Should().BeTrue();
        outcome.Summary.Should().Be("Authentication Administrator activated for 2 hours");
        var body = _http.Requests.Single().Body!;
        body.Should().Contain("\"action\":\"selfActivate\"").And.Contain("\"principalId\":\"me-1\"").And.Contain("\"directoryScopeId\":\"/\"")
            .And.Contain("\"duration\":\"PT2H\"").And.Contain("\"justification\":\"Ticket work\"").And.Contain("\"ticketNumber\":\"INC-9\"");
        _actions.Query(new ActionLogQuery { ActionPrefix = "pim.activate" }).Single().Outcome.Should().Be(ActionOutcome.Success);
    }

    [Fact]
    public async Task Activate_PendingApproval_SaysItIsWaiting()
    {
        _http.Enqueue(HttpStatusCode.Created, """{"id":"req-1","status":"PendingApproval"}""");
        var role = new PimRole { RoleDefinitionId = AuthAdmin, RoleTemplateId = AuthAdmin, DisplayName = "Authentication Administrator" };

        var outcome = await _sut.ActivateAsync(new PimActivation { Role = role, Duration = TimeSpan.FromHours(1) }, Ct);

        outcome.Summary.Should().Contain("waiting for approval");
    }

    [Fact]
    public async Task Activate_NeedingFreshMfa_SaysWhatToDo()
    {
        _http.Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":"RoleAssignmentRequestAcrsValidationFailed","message":"The role assignment request failed the ACRS validation."}}""");
        var role = new PimRole { RoleDefinitionId = AuthAdmin, RoleTemplateId = AuthAdmin, DisplayName = "Authentication Administrator" };

        var outcome = await _sut.ActivateAsync(new PimActivation { Role = role, Duration = TimeSpan.FromHours(1) }, Ct);

        outcome.Success.Should().BeFalse();
        outcome.FriendlyError.Should().Contain("multi-factor");
    }
}
