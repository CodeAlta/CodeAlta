using CodeAlta.Agent;
using CodeAlta.Desktop;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

// The permission requests the page of the phones reads and answers, over functions a test gives.
[TestClass]
public sealed class DesktopPluginPermissionsTests
{
    private static SessionOwnedPermissionSnapshot Entry(string session = "session", SessionPermissionSkill? skill = null)
    {
        var attempt = new SessionPermissionHandle(session, null, "interaction", Guid.NewGuid());
        return new(new(Guid.NewGuid(), Guid.NewGuid(), 1, attempt),
            new(attempt, new ModelProviderId("fake"), DateTimeOffset.UnixEpoch, "commandExecution", "git push", "Q:\\fixture", "Why", null)
            { Shortened = true, OutsideSandbox = true, Skill = skill });
    }

    private sealed class Host
    {
        internal Dictionary<string, SessionOwnedPermissionSnapshot[]> Pending { get; } = new(StringComparer.Ordinal);
        internal List<string> Waiting { get; } = [];
        internal List<(SessionOwnedPermissionHandle Handle, AgentPermissionDecisionKind Decision)> Resolved { get; } = [];
        internal Func<bool> Accept { get; set; } = static () => true;

        internal DesktopPluginPermissions Service()
        {
            var service = new DesktopPluginPermissions();
            service.Attach(
                () => ValueTask.FromResult<IReadOnlyList<string>>([.. Waiting]),
                (id, _) => ValueTask.FromResult(new SessionOwnedPermissionPage(Pending.GetValueOrDefault(id) ?? [], Pending.GetValueOrDefault(id)?.Length > 3)),
                (handle, decision, _) => { Resolved.Add((handle, decision)); return ValueTask.FromResult(Accept()); });
            return service;
        }
    }

    [TestMethod]
    public void OnlyTheBuiltInMobilePlugin_GetsThePermissionRequests()
    {
        var service = new DesktopPluginPermissions();
        Assert.AreSame(service, service.ForPlugin("builtin:mobile", builtIn: true));
        Assert.AreSame(NoopPluginPermissionService.Instance, service.ForPlugin("builtin:mobile", builtIn: false), "a plugin of a folder whose key looks like it");
        Assert.AreSame(NoopPluginPermissionService.Instance, service.ForPlugin("builtin:git", builtIn: true));
        Assert.AreSame(NoopPluginPermissionService.Instance, service.ForPlugin("MyPlugin:MyPlugin.Answers", builtIn: false));
        Assert.ThrowsExactly<ArgumentException>(() => service.ForPlugin(" ", builtIn: true));
    }

    [TestMethod]
    public async Task BeforeTheHostRuns_NothingWaits_AndNothingCanBeAnswered()
    {
        var service = new DesktopPluginPermissions();
        Assert.IsFalse(service.HasPermissions);
        Assert.IsEmpty(await service.ListWaitingAsync());
        Assert.IsEmpty((await service.ListAsync("session")).Requests);
        Assert.AreEqual("unavailable", (await service.ResolveAsync("session", Guid.NewGuid().ToString(), PluginPermissionDecision.AllowOnce)).Status);
    }

    [TestMethod]
    public async Task ASessionThatWaits_IsListedWithItsFirstRequest_AndOneThatWaitsForAFormIsNot()
    {
        var host = new Host();
        var first = Entry();
        host.Pending["session"] = [first, Entry()];
        host.Waiting.AddRange(["session", "form-only"]);
        var summary = (await host.Service().ListWaitingAsync()).Single();
        Assert.AreEqual(new PluginPermissionSummary("session", first.Handle.Attempt.AttemptId.ToString("D"), 2), summary);
    }

    [TestMethod]
    public async Task ARequest_IsReadWithWhatTheWindowShowsOfIt()
    {
        var host = new Host();
        var entry = Entry(skill: new("hf", "transformers", "skill://hf/x", 1, 2, false));
        host.Pending["session"] = [entry];
        var page = await host.Service().ListAsync("session");
        Assert.IsFalse(page.HasMore);
        var request = page.Requests.Single();
        Assert.AreEqual(entry.Handle.Attempt.AttemptId.ToString("D"), request.RequestId);
        Assert.AreEqual(("commandExecution", "git push", "Q:\\fixture", "Why"), (request.Kind, request.Command, request.WorkingDirectory, request.Reason));
        Assert.IsTrue(request.Shortened);
        Assert.IsTrue(request.OutsideSandbox);
        Assert.AreEqual(("hf", "transformers"), (request.SkillServer, request.SkillName));
    }

    [TestMethod]
    public async Task AnAnswer_GoesToTheRequestThatWaits_WithTheChoiceOfTheUser()
    {
        var host = new Host();
        var (first, second) = (Entry(), Entry());
        host.Pending["session"] = [first, second];
        var service = host.Service();
        Assert.AreEqual("resolved", (await service.ResolveAsync("session", second.Handle.Attempt.AttemptId.ToString(), PluginPermissionDecision.Deny)).Status);
        Assert.AreEqual((second.Handle, AgentPermissionDecisionKind.Deny), host.Resolved.Single());
        Assert.AreEqual("resolved", (await service.ResolveAsync("session", first.Handle.Attempt.AttemptId.ToString(), PluginPermissionDecision.AllowOnce)).Status);
        Assert.AreEqual((first.Handle, AgentPermissionDecisionKind.AllowOnce), host.Resolved[1]);
    }

    [TestMethod]
    public async Task AnAnswer_IsNotSent_ForARequestThatNoLongerWaits_OrOfAnotherSession_OrNotNamedProperly()
    {
        var host = new Host();
        var entry = Entry();
        host.Pending["session"] = [entry];
        host.Pending["other"] = [Entry("other")];
        var service = host.Service();
        Assert.AreEqual("rejected", (await service.ResolveAsync("session", Guid.NewGuid().ToString(), PluginPermissionDecision.AllowOnce)).Status);
        Assert.AreEqual("rejected", (await service.ResolveAsync("other", entry.Handle.Attempt.AttemptId.ToString(), PluginPermissionDecision.AllowOnce)).Status, "A request of another session.");
        foreach (var id in new[] { "", "not-a-guid", Guid.Empty.ToString() })
            Assert.AreEqual("invalid", (await service.ResolveAsync("session", id, PluginPermissionDecision.AllowOnce)).Status);
        Assert.AreEqual("invalid", (await service.ResolveAsync("bad\nsession", entry.Handle.Attempt.AttemptId.ToString(), PluginPermissionDecision.AllowOnce)).Status);
        Assert.AreEqual("invalid", (await service.ResolveAsync("session", entry.Handle.Attempt.AttemptId.ToString(), (PluginPermissionDecision)99)).Status);
        Assert.IsEmpty(host.Resolved, "Nothing refused reaches the session.");
    }

    [TestMethod]
    public async Task AnAnswerTheHostRefuses_IsRejected_AndAFailure_IsNotTakenForARefusal()
    {
        var host = new Host();
        var entry = Entry();
        host.Pending["session"] = [entry];
        var service = host.Service();
        host.Accept = static () => false;
        Assert.AreEqual("rejected", (await service.ResolveAsync("session", entry.Handle.Attempt.AttemptId.ToString(), PluginPermissionDecision.AllowOnce)).Status);
        host.Accept = static () => throw new InvalidOperationException("The mailbox closed.");
        Assert.AreEqual("uncertain", (await service.ResolveAsync("session", entry.Handle.Attempt.AttemptId.ToString(), PluginPermissionDecision.AllowOnce)).Status);
    }
}
