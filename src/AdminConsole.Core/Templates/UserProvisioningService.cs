using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.Templates;

/// <summary>
/// Creating a user from a template (doc 07 §Create-from-template flow step 4): create, licence, groups, manager, in
/// that order. A failed create halts, because everything after it would be operating on an account that is not there.
/// A failure later does not halt: a user missing one group is a smaller problem than a user who half exists.
/// </summary>
public sealed class UserProvisioningService
{
    private readonly GraphClient _graph;
    private readonly CacheDb _db;
    private readonly ActionLog _actions;
    private readonly TenantContext _tenant;
    private readonly ILogger<UserProvisioningService> _logger;

    /// <summary>
    /// <paramref name="delay"/> exists so tests do not have to wait out the retry backoff; production passes null and
    /// gets <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </summary>
    public UserProvisioningService(GraphClient graph, CacheDb db, ActionLog actions, TenantContext tenant, ILogger<UserProvisioningService> logger, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _graph = graph;
        _db = db;
        _actions = actions;
        _tenant = tenant;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Entra needs a moment after creating a user before it will accept a licence for them, and answers 409
    /// <c>Directory_ConcurrencyViolation</c> until it does. Verified on the dev tenant: the first of three creates hit
    /// it, the others did not. The service's own message says to wait briefly and retry, so that is what this does.
    /// </summary>
    public const int MaxLicenceAttempts = 4;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // ---- uniqueness checks (doc 07 §flow step 2) --------------------------------------------------------------------

    /// <summary>
    /// Whether a UPN is free. Answers <see cref="UniquenessResult.Unknown"/> rather than guessing when Graph cannot be
    /// reached: telling an admin a UPN is available when nobody checked is worse than admitting the check failed.
    /// </summary>
    public async Task<UniquenessResult> CheckUpnAsync(string upn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(upn) || !upn.Contains('@', StringComparison.Ordinal))
        {
            return UniquenessResult.Invalid("That is not a complete sign-in name yet.");
        }

        return await CountAsync($"users?$filter=userPrincipalName eq '{Escape(upn)}'&$count=true&$top=1", "sign-in name", ct).ConfigureAwait(false);
    }

    /// <summary>Whether an address is already somebody's proxy address, which would make the mailbox collide.</summary>
    public async Task<UniquenessResult> CheckProxyAddressAsync(string address, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address) || !address.Contains('@', StringComparison.Ordinal)) return UniquenessResult.Available;
        return await CountAsync($"users?$filter=proxyAddresses/any(p:p eq 'SMTP:{Escape(address)}')&$count=true&$top=1", "email address", ct).ConfigureAwait(false);
    }

    private async Task<UniquenessResult> CountAsync(string url, string what, CancellationToken ct)
    {
        try
        {
            var page = await _graph.GetPageAsync(url, ct).ConfigureAwait(false);
            return page.Items.Count == 0
                ? UniquenessResult.Available
                : UniquenessResult.Taken($"That {what} is already in use.");
        }
        catch (GraphException ex)
        {
            _logger.LogWarning("Uniqueness check failed: {Code}", ex.ErrorCode);
            return UniquenessResult.Unknown($"Could not check whether that {what} is free ({GraphErrorText.Friendly(ex)})");
        }
    }

    /// <summary>Doubles apostrophes, which is how OData escapes them inside a quoted string.</summary>
    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    // ---- create (doc 07 §flow step 4) -------------------------------------------------------------------------------

    public async Task<CreateUserResult> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var steps = new List<CreateStepResult>(4);
        var correlationId = $"create-{Guid.NewGuid():N}";
        using var scope = ActionLog.BeginCorrelation(correlationId);

        DirectoryUser created;
        try
        {
            created = await PostUserAsync(request, ct).ConfigureAwait(false);
            steps.Add(CreateStepResult.Ok("Create the account", $"Created {created.UserPrincipalName}"));
        }
        catch (GraphException ex)
        {
            Record(request.UserPrincipalName, request.UserPrincipalName, "user.create", $"POST users ({request.UserPrincipalName})", ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId);
            _logger.LogWarning("Creating {Upn} failed: {Code}", request.UserPrincipalName, ex.ErrorCode);
            steps.Add(CreateStepResult.Fail("Create the account", GraphErrorText.Friendly(ex, Capabilities.CreateUser.RoleHint), ex));
            steps.Add(new CreateStepResult("Assign licences", StepState.NotRun, "Not run — the account was not created"));
            steps.Add(new CreateStepResult("Add to groups", StepState.NotRun, "Not run — the account was not created"));
            steps.Add(new CreateStepResult("Set the manager", StepState.NotRun, "Not run — the account was not created"));
            return new CreateUserResult { Success = false, Steps = steps, Elapsed = clock.Elapsed };
        }

        steps.Add(await AssignLicensesAsync(created, request, ct).ConfigureAwait(false));
        steps.Add(await AddGroupsAsync(created, request, ct).ConfigureAwait(false));
        steps.Add(await SetManagerAsync(created, request, ct).ConfigureAwait(false));

        _logger.LogInformation("Created {UserId} in {Ms} ms (correlation {Correlation})", created.Id, clock.ElapsedMilliseconds, correlationId);

        return new CreateUserResult
        {
            Success = true,
            UserId = created.Id,
            UserPrincipalName = created.UserPrincipalName,
            Password = request.Password,
            Steps = steps,
            Elapsed = clock.Elapsed,
        };
    }

    private async Task<DirectoryUser> PostUserAsync(CreateUserRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["accountEnabled"] = true,
            ["displayName"] = request.DisplayName,
            ["givenName"] = request.GivenName,
            ["surname"] = request.Surname,
            ["userPrincipalName"] = request.UserPrincipalName,
            ["mailNickname"] = request.MailNickname,
            ["passwordProfile"] = new Dictionary<string, object?>
            {
                ["password"] = request.Password,
                ["forceChangePasswordNextSignIn"] = request.ForceChangePasswordNextSignIn,
            },
        };

        foreach (var (key, value) in request.Attributes)
        {
            // The form owns the identity fields; a template must not be able to override the name that was typed.
            if (Reserved.Contains(key)) continue;
            body[key] = value;
        }

        using var doc = await _graph.SendAsync(HttpMethod.Post, "users", body, ct).ConfigureAwait(false)
            ?? throw new GraphException(0, "EmptyResponse", "Graph created the user but returned no body.");

        var user = DirectoryUser.FromJson(doc.RootElement);
        _db.Write(w => w.UpsertUser(user));

        // The request summary is free text, so the password must never be built into it (see UserService.IsSecret).
        Record(user.Id, user.UserPrincipalName, "user.create", $"POST users ({user.UserPrincipalName})", ActionOutcome.Success, $"Created {user.DisplayName}");
        return user;
    }

    /// <summary>Identity fields the form owns; a template carrying one of these is ignored rather than obeyed.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "accountEnabled", "displayName", "givenName", "surname", "userPrincipalName",
        "mailNickname", "passwordProfile", "id", "mail", "proxyAddresses",
    };

    /// <summary>
    /// The cloud half of a hybrid create (M17.3), once Entra Connect has brought the account up: usage location, licences,
    /// cloud groups. Not the manager — a synced user's manager is mastered in AD and was set there at create.
    /// </summary>
    public async Task<IReadOnlyList<CreateStepResult>> FinishCloudAsync(DirectoryUser user, string? usageLocation, IReadOnlyList<string> skuIds,
        IReadOnlyList<TemplateGroup> groups, CancellationToken ct)
    {
        var steps = new List<CreateStepResult>(3);
        if (!string.IsNullOrWhiteSpace(usageLocation))
        {
            try
            {
                await _graph.SendAsync(HttpMethod.Patch, $"users/{user.Id}", new Dictionary<string, object?> { ["usageLocation"] = usageLocation }, ct).ConfigureAwait(false);
                Record(user.Id, user.UserPrincipalName, "user.patch.usageLocation", $"PATCH usageLocation={usageLocation}", ActionOutcome.Success, $"Usage location {usageLocation}");
                steps.Add(CreateStepResult.Ok("Set the usage location", $"Usage location {usageLocation}"));
            }
            catch (GraphException ex)
            {
                steps.Add(CreateStepResult.Fail("Set the usage location", GraphErrorText.Friendly(ex), ex));
            }
        }

        var request = new CreateUserRequest
        {
            DisplayName = user.DisplayName ?? user.UserPrincipalName ?? user.Id,
            GivenName = string.Empty,
            Surname = string.Empty,
            UserPrincipalName = user.UserPrincipalName ?? user.Id,
            MailNickname = string.Empty,
            Password = string.Empty,
            SkuIds = skuIds,
            Groups = groups,
            Attributes = usageLocation is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["usageLocation"] = usageLocation },
        };
        steps.Add(await AssignLicensesAsync(user, request, ct).ConfigureAwait(false));
        steps.Add(await AddGroupsAsync(user, request, ct).ConfigureAwait(false));
        return steps;
    }

    private async Task<CreateStepResult> AssignLicensesAsync(DirectoryUser user, CreateUserRequest request, CancellationToken ct)
    {
        const string title = "Assign licences";
        if (request.SkuIds.Count == 0) return CreateStepResult.Skip(title, "No licences in the template");

        if (string.IsNullOrWhiteSpace(request.UsageLocation))
        {
            return CreateStepResult.Skip(title, "Skipped — a usage location is needed before a licence can be assigned");
        }

        var body = new Dictionary<string, object?>
        {
            ["addLicenses"] = request.SkuIds.Select(id => new Dictionary<string, object?> { ["skuId"] = id, ["disabledPlans"] = Array.Empty<string>() }).ToArray(),
            ["removeLicenses"] = Array.Empty<string>(),
        };

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var doc = await _graph.SendAsync(HttpMethod.Post, $"users/{user.Id}/assignLicense", body, ct).ConfigureAwait(false);
                var names = request.SkuIds.Select(NameOf).ToList();
                Record(user.Id, user.UserPrincipalName, "license.assign", $"POST assignLicense ({request.SkuIds.Count})", ActionOutcome.Success, string.Join(", ", names));
                return CreateStepResult.Ok(title, $"Assigned {names.Count} licence{(names.Count == 1 ? string.Empty : "s")}: {string.Join(", ", names)}");
            }
            catch (GraphException ex) when (IsStillSettling(ex) && attempt < MaxLicenceAttempts)
            {
                var wait = TimeSpan.FromSeconds(attempt);
                _logger.LogInformation("Licence assignment for {UserId} lost a race with directory replication; retrying in {Seconds} s", user.Id, wait.TotalSeconds);
                await _delay(wait, ct).ConfigureAwait(false);
            }
            catch (GraphException ex)
            {
                Record(user.Id, user.UserPrincipalName, "license.assign", $"POST assignLicense ({request.SkuIds.Count})", ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId);
                return CreateStepResult.Fail(title, GraphErrorText.Friendly(ex, Capabilities.AssignLicense.RoleHint), ex);
            }
        }
    }

    private async Task<CreateStepResult> AddGroupsAsync(DirectoryUser user, CreateUserRequest request, CancellationToken ct)
    {
        const string title = "Add to groups";
        if (request.Groups.Count == 0) return CreateStepResult.Skip(title, "No groups in the template");

        var requests = request.Groups
            .Select(g => BatchRequest.Post(g.Id, $"/groups/{g.Id}/members/$ref",
                new Dictionary<string, object?> { ["@odata.id"] = $"{GraphClient.V1BaseAddress}directoryObjects/{user.Id}" }))
            .ToList();

        try
        {
            var responses = await _graph.BatchAsync(requests, ct).ConfigureAwait(false);
            var added = new List<string>();
            var failed = new List<string>();

            foreach (var group in request.Groups)
            {
                var label = group.DisplayName ?? group.Id;
                if (responses.TryGetValue(group.Id, out var response) && response.IsSuccess)
                {
                    added.Add(label);
                    Record(group.Id, label, "group.member.add", $"POST members/$ref ({user.Id})", ActionOutcome.Success, $"Added {user.DisplayName} to {label}", targetType: ObjectType.Group);
                }
                else
                {
                    var why = response is null ? "no response" : $"HTTP {response.Status}";
                    failed.Add($"{label} ({why})");
                    Record(group.Id, label, "group.member.add", $"POST members/$ref ({user.Id})", ActionOutcome.Failed, why, targetType: ObjectType.Group);
                }
            }

            if (added.Count == 0) return CreateStepResult.Fail(title, $"Could not add to any group: {string.Join("; ", failed)}");
            var message = $"Added to {added.Count} group{(added.Count == 1 ? string.Empty : "s")}: {string.Join(", ", added)}";
            return failed.Count == 0
                ? CreateStepResult.Ok(title, message)
                : CreateStepResult.Fail(title, $"{message}, but {failed.Count} failed: {string.Join("; ", failed)}");
        }
        catch (GraphException ex)
        {
            return CreateStepResult.Fail(title, GraphErrorText.Friendly(ex, Capabilities.ChangeGroupMembership.RoleHint), ex);
        }
    }

    private async Task<CreateStepResult> SetManagerAsync(DirectoryUser user, CreateUserRequest request, CancellationToken ct)
    {
        const string title = "Set the manager";
        if (string.IsNullOrWhiteSpace(request.ManagerId)) return CreateStepResult.Skip(title, "No manager in the template");

        try
        {
            var body = new Dictionary<string, object?> { ["@odata.id"] = $"{GraphClient.V1BaseAddress}users/{request.ManagerId}" };
            await _graph.SendAsync(HttpMethod.Put, $"users/{user.Id}/manager/$ref", body, ct).ConfigureAwait(false);
            Record(user.Id, user.UserPrincipalName, "user.manager.set", $"PUT manager/$ref ({request.ManagerId})", ActionOutcome.Success, "Manager set");
            return CreateStepResult.Ok(title, "Manager set");
        }
        catch (GraphException ex)
        {
            Record(user.Id, user.UserPrincipalName, "user.manager.set", $"PUT manager/$ref ({request.ManagerId})", ActionOutcome.Failed, GraphErrorText.Friendly(ex), ex.RequestId);
            return CreateStepResult.Fail(title, GraphErrorText.Friendly(ex, Capabilities.EditUser.RoleHint), ex);
        }
    }

    /// <summary>A conflict that means "the directory has not caught up yet", as opposed to a real one.</summary>
    private static bool IsStillSettling(GraphException ex) =>
        ex.StatusCode == 409 && ex.ErrorCode is "Directory_ConcurrencyViolation" or "Request_MultipleObjectsWithSameKeyValue";

    private string NameOf(string skuId) => _db.GetSkus().FirstOrDefault(s => string.Equals(s.SkuId, skuId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? skuId;

    private void Record(string targetId, string? targetName, string action, string request, ActionOutcome outcome, string? message, string? requestId = null, ObjectType targetType = ObjectType.User) =>
        _actions.Record(new ActionRecord
        {
            Actor = _tenant.SignedInUpn,
            TenantId = _tenant.TenantId,
            TargetType = targetType,
            TargetId = targetId,
            TargetName = targetName,
            Action = action,
            RequestSummary = request,
            Outcome = outcome,
            Message = message,
            GraphRequestId = requestId,
        });
}

/// <summary>The answer to "is this name free" (doc 07 §flow step 2).</summary>
public sealed record UniquenessResult
{
    public required UniquenessState State { get; init; }
    public string? Message { get; init; }

    public bool BlocksCreate => State is UniquenessState.Taken or UniquenessState.Invalid;

    public static UniquenessResult Available { get; } = new() { State = UniquenessState.Available };
    public static UniquenessResult Taken(string message) => new() { State = UniquenessState.Taken, Message = message };
    public static UniquenessResult Invalid(string message) => new() { State = UniquenessState.Invalid, Message = message };
    public static UniquenessResult Unknown(string message) => new() { State = UniquenessState.Unknown, Message = message };
}

public enum UniquenessState
{
    Available,
    Taken,
    Invalid,

    /// <summary>The check itself failed. Never reported as available: nobody actually looked.</summary>
    Unknown,
}
