using FluentAssertions;
using Xunit;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.Tests.Cache;

public sealed class ActionLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly ActionLog _log = ActionLog.OpenInMemory();

    public void Dispose() => _log.Dispose();

    private ActionRecord Row(string action = "user.patch", string? correlationId = null) => new()
    {
        Actor = "admin@contoso.com",
        TenantId = "t1",
        TargetType = ObjectType.User,
        TargetId = "u1",
        Action = action,
        RequestSummary = "PATCH accountEnabled=false",
        Outcome = ActionOutcome.Success,
        CorrelationId = correlationId,
    };

    [Fact]
    public void BeginCorrelation_StampsEveryRowWrittenInsideTheScope()
    {
        // M6: an offboarding run groups its rows this way, and the steps call the ordinary services, which know
        // nothing about runs.
        long inside1, inside2, outside;
        using (ActionLog.BeginCorrelation("offboard-7"))
        {
            inside1 = _log.Record(Row("user.disable"));
            inside2 = _log.Record(Row("license.remove"));
        }

        outside = _log.Record(Row("user.patch.jobTitle"));

        _log.Get(inside1)!.CorrelationId.Should().Be("offboard-7");
        _log.Get(inside2)!.CorrelationId.Should().Be("offboard-7");
        _log.Get(outside)!.CorrelationId.Should().BeNull("the scope ended");
    }

    [Fact]
    public void BeginCorrelation_DoesNotOverrideARowThatBroughtItsOwnId()
    {
        using var scope = ActionLog.BeginCorrelation("ambient");

        var id = _log.Record(Row(correlationId: "explicit"));

        _log.Get(id)!.CorrelationId.Should().Be("explicit");
    }

    [Fact]
    public void BeginCorrelation_NestsAndRestoresThePreviousScope()
    {
        long outer1, inner, outer2;
        using (ActionLog.BeginCorrelation("outer"))
        {
            outer1 = _log.Record(Row());
            using (ActionLog.BeginCorrelation("inner")) inner = _log.Record(Row());
            outer2 = _log.Record(Row());
        }

        _log.Get(outer1)!.CorrelationId.Should().Be("outer");
        _log.Get(inner)!.CorrelationId.Should().Be("inner");
        _log.Get(outer2)!.CorrelationId.Should().Be("outer");
    }

    [Fact]
    public void Record_AssignsIds_AndGetRoundTripsEveryField()
    {
        var id = _log.Record(new ActionRecord
        {
            Timestamp = T0,
            Actor = "admin@contoso.com",
            TenantId = "t1",
            TargetType = ObjectType.User,
            TargetId = "u1",
            TargetName = "Ada Lovelace",
            Action = "user.disable",
            RequestSummary = "PATCH accountEnabled=false",
            Outcome = ActionOutcome.Success,
            Message = "Disabled",
            GraphRequestId = "req-1",
            InverseAction = "user.patch",
            InversePayload = """{"accountEnabled":true}""",
            CorrelationId = "offboard-42",
        });

        id.Should().BeGreaterThan(0);
        var a = _log.Get(id)!;
        a.Should().BeEquivalentTo(new
        {
            Id = id,
            Timestamp = T0,
            Actor = "admin@contoso.com",
            TenantId = "t1",
            TargetType = ObjectType.User,
            TargetId = "u1",
            TargetName = "Ada Lovelace",
            Action = "user.disable",
            RequestSummary = "PATCH accountEnabled=false",
            Outcome = ActionOutcome.Success,
            Message = "Disabled",
            GraphRequestId = "req-1",
            InverseAction = "user.patch",
            InversePayload = """{"accountEnabled":true}""",
            CorrelationId = "offboard-42",
            UndoneAt = (DateTimeOffset?)null,
        });
        a.IsReversible.Should().BeTrue();
        _log.Get(999).Should().BeNull();
    }

    [Fact]
    public void Query_FiltersAndOrdersNewestFirst()
    {
        Seed();

        var forUser = _log.Query(new ActionLogQuery { TargetId = "u1" });
        forUser.Select(a => a.Action).Should().Equal("user.license.remove", "user.disable");

        _log.Query(new ActionLogQuery { Outcome = ActionOutcome.Failed }).Should().ContainSingle().Which.Action.Should().Be("device.wipe");
        _log.Query(new ActionLogQuery { ActionPrefix = "user." }).Should().HaveCount(3);
        _log.Query(new ActionLogQuery { TargetType = ObjectType.Device }).Should().HaveCount(1);
        _log.Query(new ActionLogQuery { CorrelationId = "run-1" }).Should().HaveCount(2);
        _log.Query(new ActionLogQuery { Actor = "other@contoso.com" }).Should().HaveCount(1);
        _log.Query(new ActionLogQuery { Since = T0.AddMinutes(2) }).Should().HaveCount(2);
        _log.Query(new ActionLogQuery { Until = T0.AddMinutes(1) }).Should().HaveCount(2);
        _log.Query(new ActionLogQuery { Limit = 2 }).Should().HaveCount(2);
        _log.Query(new ActionLogQuery { Reversible = true }).Select(a => a.Action).Should().BeEquivalentTo(["user.disable", "user.license.remove"]);
        _log.Query(new ActionLogQuery { Reversible = false }).Should().HaveCount(2);
        _log.Count().Should().Be(4);
    }

    [Fact]
    public void MarkUndone_ClearsReversibility_Once()
    {
        Seed();
        var disable = _log.Query(new ActionLogQuery { ActionPrefix = "user.disable" }).Single();

        _log.MarkUndone(disable.Id, T0.AddHours(1)).Should().BeTrue();
        _log.MarkUndone(disable.Id, T0.AddHours(2)).Should().BeFalse("already undone");
        _log.MarkUndone(12345).Should().BeFalse();

        var after = _log.Get(disable.Id)!;
        after.UndoneAt.Should().Be(T0.AddHours(1));
        after.IsReversible.Should().BeFalse();
        _log.Query(new ActionLogQuery { Reversible = true }).Should().ContainSingle().Which.Action.Should().Be("user.license.remove");
    }

    [Fact]
    public void Record_RedactsSecretsInSummaryAndInversePayload()
    {
        var id = _log.Record(new ActionRecord
        {
            Actor = "a",
            TenantId = "t",
            TargetType = ObjectType.User,
            TargetId = "u",
            Action = "user.password.reset",
            RequestSummary = """{"passwordProfile":{"password":"Hunter2!","forceChangePasswordNextSignIn":true}}""",
            Outcome = ActionOutcome.Success,
            InverseAction = "none",
            InversePayload = """[{"token":"eyJ...","nested":{"secretKey":"abc","fine":"keep"}}]""",
        });

        var a = _log.Get(id)!;
        a.RequestSummary.Should().Be("""{"passwordProfile":"***"}""");
        a.InversePayload.Should().Be("""[{"token":"***","nested":{"secretKey":"***","fine":"keep"}}]""");
        a.RequestSummary.Should().NotContain("Hunter2");
        a.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "default timestamp is now");
    }

    [Fact]
    public void Redact_LeavesNonJsonAndCleanJsonAlone()
    {
        ActionLog.Redact(null).Should().BeNull();
        ActionLog.Redact("PATCH accountEnabled=false").Should().Be("PATCH accountEnabled=false");
        ActionLog.Redact("""{"accountEnabled":true}""").Should().Be("""{"accountEnabled":true}""");
        ActionLog.Redact("{not json").Should().Be("{not json");
        ActionLog.Redact("""{"Password":"x"}""").Should().Be("""{"Password":"***"}""", "key match is case-insensitive");
    }

    [Fact]
    public void ToMarkdown_RendersATableAndEscapesPipes()
    {
        Seed();
        var md = ActionLog.ToMarkdown(_log.Query(new ActionLogQuery { CorrelationId = "run-1" }));

        md.Should().StartWith("| Time (UTC) | Action | Target | Outcome | Message |");
        md.Should().Contain("| user.license.remove | Ada Lovelace (`u1`) | Success | Removed E3 \\| kept F3 |");
        md.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(4);
    }

    [Fact]
    public void OpenFile_CreatesTheDatabaseAndSchema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yl-actions-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var log = ActionLog.OpenFile(path))
            {
                log.Record(new ActionRecord { Actor = "a", TenantId = "t", TargetType = ObjectType.Group, TargetId = "g", Action = "group.member.add", Outcome = ActionOutcome.Success });
                log.Count().Should().Be(1);
            }

            using (var again = ActionLog.OpenFile(path))
            {
                again.Count().Should().Be(1, "schema creation is idempotent and data persists");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(f)) File.Delete(f);
            }
        }
    }

    private void Seed()
    {
        _log.Record(new ActionRecord { Timestamp = T0, Actor = "admin@contoso.com", TenantId = "t", TargetType = ObjectType.User, TargetId = "u1", TargetName = "Ada Lovelace", Action = "user.disable", Outcome = ActionOutcome.Success, InverseAction = "user.patch", InversePayload = """{"accountEnabled":true}""", CorrelationId = "run-1" });
        _log.Record(new ActionRecord { Timestamp = T0.AddMinutes(1), Actor = "admin@contoso.com", TenantId = "t", TargetType = ObjectType.User, TargetId = "u1", TargetName = "Ada Lovelace", Action = "user.license.remove", Outcome = ActionOutcome.Success, Message = "Removed E3 | kept F3", InverseAction = "license.assign", InversePayload = """{"addLicenses":[{"skuId":"s1"}]}""", CorrelationId = "run-1" });
        _log.Record(new ActionRecord { Timestamp = T0.AddMinutes(2), Actor = "admin@contoso.com", TenantId = "t", TargetType = ObjectType.Device, TargetId = "d1", Action = "device.wipe", Outcome = ActionOutcome.Failed, Message = "403 Authorization_RequestDenied", GraphRequestId = "r-9" });
        _log.Record(new ActionRecord { Timestamp = T0.AddMinutes(3), Actor = "other@contoso.com", TenantId = "t", TargetType = ObjectType.User, TargetId = "u2", Action = "user.patch", Outcome = ActionOutcome.Skipped, Message = "Mastered in on-prem AD" });
    }
}
