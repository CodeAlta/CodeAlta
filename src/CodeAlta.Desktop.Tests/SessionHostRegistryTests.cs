using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Orchestration.Jobs;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

// The registry of session hosts and the services of the page that go through it, over hosts that record what they are asked.
[TestClass]
public sealed class SessionHostRegistryTests
{
    private const string Epoch = "00000000-0000-0000-0000-000000000001";
    private const string SessionId = "6f1c8a52-3a4b-4d6e-9f10-1a2b3c4d5e6f";

    [TestMethod]
    public void Resolve_GivesTheLocalHostToASessionOfNoMachine_AndTheHostOfItsMachineOtherwise()
    {
        var local = new RecordingSessionHost(null);
        var remote = new RecordingSessionHost("mac-mini");
        var registry = new SessionHostRegistry(local, [remote]);

        Assert.AreSame(local, registry.Local);
        Assert.AreSame(local, registry.Resolve(SessionId));
        Assert.AreSame(local, registry.Resolve(new SessionReference(SessionId)));
        Assert.AreSame(remote, registry.Resolve(new SessionReference(SessionId, "mac-mini")));
        Assert.AreSame(local, registry.ResolveOperation(Guid.NewGuid()));
        Assert.IsTrue(new SessionReference(SessionId).IsLocal);
        Assert.IsFalse(new SessionReference(SessionId, "mac-mini").IsLocal);
        CollectionAssert.AreEqual(new[] { remote }, registry.Remotes.ToArray());
    }

    [TestMethod]
    public void Resolve_RefusesAMachineItHasNoHostFor()
    {
        var registry = new SessionHostRegistry(new RecordingSessionHost(null));

        Assert.IsFalse(registry.TryResolve(new SessionReference(SessionId, "elsewhere"), out var host));
        Assert.IsNull(host);
        Assert.ThrowsExactly<KeyNotFoundException>(() => registry.Resolve(new SessionReference(SessionId, "elsewhere")));
    }

    [TestMethod]
    public void Constructor_RefusesHostsThatNameTheWrongMachines()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new SessionHostRegistry(null!));
        Assert.ThrowsExactly<ArgumentException>(() => new SessionHostRegistry(new RecordingSessionHost("mac-mini")));
        Assert.ThrowsExactly<ArgumentException>(() => new SessionHostRegistry(new RecordingSessionHost(null), [new RecordingSessionHost(null)]));
        Assert.ThrowsExactly<ArgumentException>(() => new SessionHostRegistry(new RecordingSessionHost(null),
            [new RecordingSessionHost("mac-mini"), new RecordingSessionHost("mac-mini")]));
    }

    [TestMethod]
    public async Task RoutedServices_AskTheHostOfTheSession()
    {
        var local = new RecordingSessionHost(null);
        var hosts = new SessionHostRegistry(local);

        await using (var display = new SessionDisplayService(hosts, Epoch).Observe(new SessionDisplayRequest(Epoch, SessionId), CancellationToken.None))
        {
            await foreach (var _ in display.Items) { }
        }
        var state = await new SessionRuntimeStateService(hosts, Epoch).CurrentAsync(new SessionRuntimeStateRequest(Epoch, SessionId), CancellationToken.None);
        var asks = await new SessionAsksService(hosts, Epoch).ListAsync(new SessionAsksRequest(Epoch, SessionId), CancellationToken.None);
        var permissions = await new SessionPermissionsService(hosts, Epoch, static () => true)
            .ListAsync(new SessionPermissionsRequest(Epoch, SessionId), CancellationToken.None);
        var inputs = await new SessionUserInputService(hosts, Epoch, enabled: true).ListAsync(new UserInputListRequest(Epoch, SessionId), CancellationToken.None);
        var job = new ToolCallsService(hosts, Epoch).ReadJob(new BackgroundJobDetailsRequest(Epoch, SessionId, SessionJobService.IdPrefix + "1"), CancellationToken.None);

        Assert.AreEqual("read_failed", state.Status);
        Assert.AreEqual("ok", asks.Status);
        Assert.AreEqual("ok", permissions.Status);
        Assert.AreEqual("ok", inputs.Status);
        Assert.AreEqual("missing_job", job.Status);
        CollectionAssert.AreEqual(new[]
        {
            $"ObserveDisplay {SessionId}",
            $"ReadRuntimeState {SessionId}",
            $"ListAsks {SessionId}",
            $"ListPermissions {SessionId}",
            $"ListUserInputs {SessionId}",
            $"FindJob {SessionId} {SessionJobService.IdPrefix}1",
        }, local.Calls.ToArray());
    }

    // A host that records the calls the tests make and refuses the others.
    private sealed class RecordingSessionHost(string? machineId) : ISessionHost
    {
        public List<string> Calls { get; } = [];

        public string? MachineId => machineId;

        public int ReceiptCapacity => 256;

        public bool AsksEnabled => true;

        public async IAsyncEnumerable<RuntimeDisplayReplacement> ObserveDisplayAsync(string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add($"ObserveDisplay {sessionId}");
            await Task.CompletedTask;
            yield break;
        }

        public Task<SessionRuntimeCurrentState> ReadRuntimeStateAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add($"ReadRuntimeState {sessionId}");
            return Task.FromException<SessionRuntimeCurrentState>(new InvalidOperationException("No runtime state in this test."));
        }

        public OwnedAskPage ListAsks(string sessionId)
        {
            Calls.Add($"ListAsks {sessionId}");
            return new(null, null, false);
        }

        public ValueTask<SessionOwnedPermissionPage> ListPermissionsAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add($"ListPermissions {sessionId}");
            return ValueTask.FromResult(new SessionOwnedPermissionPage([], false));
        }

        public ValueTask<SessionOwnedUserInputPage> ListUserInputsAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add($"ListUserInputs {sessionId}");
            return ValueTask.FromResult(new SessionOwnedUserInputPage([], false));
        }

        public SessionJob? FindJob(string sessionId, string jobId)
        {
            Calls.Add($"FindJob {sessionId} {jobId}");
            return null;
        }

        public Task<OwnedWorkspaceSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentSessionHistoryPage> ReadHistoryPageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentSessionHistoryPage> ReadHistoryTailPageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentSessionHistoryPage> ReadTimelinePageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OwnedTimelineTail> ReadTimelineTailAsync(string sessionId, string? knownStamp, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentHistorySourceChunk> ReadHistorySourceAsync(AgentHistoryRevision revision, long start, long end, long offset, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentEvent?> ReadHistoryRecordAsync(string sessionId, long offset, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentSystemPromptRecord> ReadSystemPromptAsync(string sessionId, long offset, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OwnedRuntimeObservation> ObserveRuntimeStateAsync(string sessionId, DateTimeOffset createdAt, string scope, string? projectId, string? projectPath,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<RuntimeToolOutputUpdate> ObserveToolOutputAsync(string sessionId, string activityId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public OwnedSessionCommandAdmission AdmitSend(OwnedTextSendRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public OwnedSessionCommandAdmission AdmitAbort(OwnedAbortRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public OwnedSessionCommandAdmission AdmitSteer(OwnedTextSteerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public OwnedSessionCommandAdmission AdmitCompact(OwnedCompactRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public OwnedSessionCommandAdmission AdmitAbortRun(OwnedAbortRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public OwnedAskDisposition? ObserveAsk(Guid actionId, OwnedAskHandle handle) => throw new NotSupportedException();
        public Task<OwnedAskDisposition> AnswerAskAsync(OwnedAskAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OwnedAskDisposition> CancelAskAsync(OwnedAskAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> ResolvePermissionAsync(SessionOwnedPermissionHandle handle, AgentPermissionDecisionKind decision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> ResolveUserInputAsync(SessionOwnedUserInputHandle handle, IReadOnlyList<SessionOwnedUserInputAnswer> answers, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> CancelUserInputAsync(SessionOwnedUserInputHandle handle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<RuntimeToolOutputUpdate> ObserveJobOutputAsync(string sessionId, string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> StopBackgroundTaskAsync(string sessionId, string taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public OwnedPermissionModeChoices GetNewSessionPermissionModes(string providerKey) => throw new NotSupportedException();
        public Task<OwnedDraftPrompts?> GetDraftPromptsAsync(OwnedProjectReferenceScope? scope, string? spaceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionViewDescriptor> CreateSessionAsync(ProjectDescriptor? project, ModelProviderDescriptor provider, string? title, string? worktreeDirectory,
            string? spaceId, string? permissionMode, AgentSessionOptions? options) => throw new NotSupportedException();
    }
}
