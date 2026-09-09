using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Tests.Fakes;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.Core.Tests.Users;

/// <summary>M5.5: remove an authentication method, revoke sessions, reset a password. None of these is reversible.</summary>
public sealed class UserServiceSecurityTests : IDisposable
{
    private const string Id = "u-1";
    private readonly FakeHttpHandler _handler = new();
    private readonly CacheDb _db = CacheDb.OpenInMemory();
    private readonly ActionLog _actions = ActionLog.OpenInMemory();
    private readonly TenantContext _tenant = new();
    private readonly UserService _sut;

    public UserServiceSecurityTests()
    {
        _tenant.Update(new TenantContextSnapshot { TenantId = "t-1", SignedInUpn = "admin@contoso.com" });
        var graph = new GraphClient(new HttpClient(_handler), new FakeAuthService(), NullLogger<GraphClient>.Instance);
        _sut = new UserService(graph, _db, _actions, _tenant, NullLogger<UserService>.Instance);
        _db.Write(w => w.UpsertUser(DirectoryUser.FromJson(Fixtures.Parse(
            """{"id":"u-1","displayName":"Ada Lovelace","userPrincipalName":"ada@contoso.com","accountEnabled":true,"assignedLicenses":[]}"""))));
    }

    public void Dispose()
    {
        _db.Dispose();
        _actions.Dispose();
    }

    private static AuthMethod Method(string odataType, string id = "m-1") =>
        AuthMethod.FromJson(Fixtures.Parse($$"""{"@odata.type":"{{odataType}}","id":"{{id}}","phoneNumber":"+1 425 555 0100","phoneType":"mobile"}"""));

    // ---- authentication methods -------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteAuthMethod_HitsTheKindsOwnCollection_AndLogsAnIrreversibleAction()
    {
        _handler.Enqueue(HttpStatusCode.NoContent);

        var r = await _sut.DeleteAuthMethodAsync(Id, Method("#microsoft.graph.phoneAuthenticationMethod"), CancellationToken.None);

        r.Success.Should().BeTrue();
        r.Summary.Should().Be("Removed Phone from Ada Lovelace");
        r.CanUndo.Should().BeFalse("only the user can register an authentication method again");

        _handler.Requests[0].Method.Should().Be(HttpMethod.Delete);
        _handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/users/u-1/authentication/phoneMethods/m-1");

        var logged = _actions.Query(new ActionLogQuery { TargetId = Id }).Single();
        logged.Action.Should().Be("user.auth.delete");
        logged.Outcome.Should().Be(ActionOutcome.Success);
        logged.IsReversible.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAuthMethod_RefusesThePasswordMethodWithoutCallingGraph()
    {
        var r = await _sut.DeleteAuthMethodAsync(Id, Method("#microsoft.graph.passwordAuthenticationMethod"), CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be("The password method can't be removed. Reset the password instead.");
        _handler.Requests.Should().BeEmpty("Graph answers 400 for this; refusing locally is faster and clearer");
        _actions.Query(new ActionLogQuery { TargetId = Id }).Single().Outcome.Should().Be(ActionOutcome.Skipped);
    }

    [Fact]
    public async Task DeleteAuthMethod_GraphRejects_MapsTheErrorWithTheRoleHint()
    {
        _handler.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""");

        var r = await _sut.DeleteAuthMethodAsync(Id, Method("#microsoft.graph.fido2AuthenticationMethod"), CancellationToken.None);

        r.Success.Should().BeFalse();
        r.FriendlyError.Should().Be("You don't have permission for this (Authentication Administrator).");
        _actions.Query(new ActionLogQuery { TargetId = Id }).Single().Outcome.Should().Be(ActionOutcome.Failed);
    }

    // ---- sessions ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RevokeSessions_PostsTheAction_AndLogsIt()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"value":true}""");

        var r = await _sut.RevokeSessionsAsync(Id, CancellationToken.None);

        r.Success.Should().BeTrue();
        r.Summary.Should().Be("Revoked all sessions for Ada Lovelace");
        r.CanUndo.Should().BeFalse();
        _handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/users/u-1/revokeSignInSessions");
        _actions.Query(new ActionLogQuery { TargetId = Id }).Single().Action.Should().Be("user.revokeSessions");
    }

    // ---- password ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResetPassword_PatchesThePasswordProfile_AndReturnsThePasswordToTheCallerOnly()
    {
        _handler.Enqueue(HttpStatusCode.NoContent);

        var r = await _sut.ResetPasswordAsync(Id, "Correct-Horse-42", forceChangeNextSignIn: true, CancellationToken.None);

        r.Outcome.Success.Should().BeTrue();
        r.Password.Should().Be("Correct-Horse-42", "the show-once dialog needs it; nothing else does");

        _handler.Requests[0].Method.Should().Be(HttpMethod.Patch);
        _handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/users/u-1");
        using var body = JsonDocument.Parse(_handler.Requests[0].Body!);
        var profile = body.RootElement.GetProperty("passwordProfile");
        profile.GetProperty("password").GetString().Should().Be("Correct-Horse-42");
        profile.GetProperty("forceChangePasswordNextSignIn").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ResetPassword_NeverPutsThePasswordInTheActionLog()
    {
        _handler.Enqueue(HttpStatusCode.NoContent);

        await _sut.ResetPasswordAsync(Id, "Correct-Horse-42", forceChangeNextSignIn: false, CancellationToken.None);

        var logged = _actions.Query(new ActionLogQuery { TargetId = Id }).Single();
        logged.RequestSummary.Should().Be("PATCH passwordProfile forceChange=False");
        logged.RequestSummary.Should().NotContain("Correct-Horse-42");
        logged.Message.Should().NotContain("Correct-Horse-42");
        logged.Outcome.Should().Be(ActionOutcome.Success);
        logged.IsReversible.Should().BeFalse();
    }

    [Fact]
    public async Task ResetPassword_TenantPolicyRejects_ReturnsTheFriendlyErrorAndNoPassword()
    {
        _handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"code":"Request_BadRequest","message":"The password does not comply with the tenant policy."}}""");

        var r = await _sut.ResetPasswordAsync(Id, "short", forceChangeNextSignIn: true, CancellationToken.None);

        r.Outcome.Success.Should().BeFalse();
        r.Password.Should().BeNull("a failed reset must not hand back a password that was never set");
        _actions.Query(new ActionLogQuery { TargetId = Id }).Single().Outcome.Should().Be(ActionOutcome.Failed);
    }

    // ---- generated passwords ----------------------------------------------------------------------------------------

    [Fact]
    public void GeneratePassword_MeetsEveryCharacterClass_AndIsNotPositional()
    {
        const string symbols = "!@#$%^&*-_=+?";
        var samples = Enumerable.Range(0, 200).Select(_ => UserService.GeneratePassword()).ToList();

        samples.Should().OnlyContain(p => p.Length == 20);
        samples.Should().OnlyContain(p => p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit) && p.Any(symbols.Contains));
        samples.Should().OnlyContain(p => !p.Any(c => "IlO01".Contains(c)), "ambiguous characters get misread when the password is read aloud");
        samples.Distinct().Should().HaveCount(samples.Count, "each call must draw fresh randomness");

        // The four guaranteed classes are shuffled, so the first position is not always an upper-case letter.
        samples.Select(p => p[0]).Should().Contain(c => !char.IsUpper(c));
    }

    [Fact]
    public void GeneratePassword_RefusesALengthTheClassesCannotFill()
    {
        var act = () => UserService.GeneratePassword(4);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
