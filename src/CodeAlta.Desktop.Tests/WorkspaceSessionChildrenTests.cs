using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceSessionChildrenTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";
    private static readonly ModelProviderDescriptor Provider = new(new("children-fixture"), "Children fixture") { IsDefault = true };

    // A chat of the Business space started a session in project A (in the Personal space) and a chat; that session
    // started a session in project B and another chat. Two sessions beside them have nothing to do with them.
    private sealed record Fixture(CodeAltaHost Host, string Root, string Global, string ProjectPath, ProjectDescriptor ProjectA, ProjectDescriptor ProjectB,
        SessionViewDescriptor Parent, SessionViewDescriptor ProjectChild, SessionViewDescriptor ChatChild,
        SessionViewDescriptor ProjectGrandchild, SessionViewDescriptor ChatGrandchild, SessionViewDescriptor Unrelated, SessionViewDescriptor UnrelatedChat);

    private static async Task<Fixture> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-children-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
        var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
        var projectPath = Directory.CreateDirectory(Path.Combine(root, "project-a")).FullName;
        var otherPath = Directory.CreateDirectory(Path.Combine(root, "project-b")).FullName;
        var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = projectPath,
            DiscoveryScope = new(Directory.CreateDirectory(Path.Combine(root, "home")).FullName, root),
            BuiltInSkillRoot = Directory.CreateDirectory(Path.Combine(root, "builtin")).FullName,
            PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false, EnableOwnedAsks = true,
            ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider, () => new DraftRuntime()),
        });
        var projectA = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
        var projectB = await host.ProjectCatalog.UpsertFromPathAsync(otherPath);
        Assert.IsTrue((await host.SpaceCatalog.CreateAsync("Business")).Succeeded);
        Assert.IsTrue((await host.SpaceCatalog.CreateAsync("Personal")).Succeeded);
        AltaActorProvenance By(SessionViewDescriptor session) => new() { Kind = "agent", SourceSessionId = session.SessionId, CreatedAt = DateTimeOffset.UtcNow };
        var parent = await host.Commands.CreateDraftSessionAsync(null, Provider, "Parent chat", spaceId: "business");
        var projectChild = await host.Commands.CreateDraftSessionAsync(projectA, Provider, "Child in A", By(parent), spaceId: "personal");
        var chatChild = await host.Commands.CreateDraftSessionAsync(null, Provider, "Child chat", By(parent), spaceId: "business");
        var projectGrandchild = await host.Commands.CreateDraftSessionAsync(projectB, Provider, "Grandchild in B", By(projectChild), spaceId: "personal");
        var chatGrandchild = await host.Commands.CreateDraftSessionAsync(null, Provider, "Grandchild chat", By(projectChild), spaceId: "personal");
        var unrelated = await host.Commands.CreateDraftSessionAsync(projectA, Provider, "Unrelated", spaceId: "personal");
        var unrelatedChat = await host.Commands.CreateDraftSessionAsync(null, Provider, "Unrelated chat", spaceId: "business");
        return new(host, root, global, projectPath, projectA, projectB, parent, projectChild, chatChild, projectGrandchild, chatGrandchild, unrelated, unrelatedChat);
    }

    [TestMethod]
    public async Task ChildSessionsAcrossProjectsChatsAndSpaces_AreShownAsATree_AndDeletedTogetherOnlyAsConfirmed()
    {
        var f = await CreateAsync();
        try
        {
            await using var host = f.Host;
            var marker = Path.Combine(f.ProjectPath, "keep.txt");
            await File.WriteAllTextAsync(marker, "source files stay");
            var rpc = new WorkspaceService(host, Epoch);

            Assert.AreEqual("stale_epoch", (await rpc.SessionChildrenAsync(new(Guid.NewGuid().ToString("D"), f.Parent.SessionId), CancellationToken.None)).Status);
            Assert.AreEqual("session_missing", (await rpc.SessionChildrenAsync(new(Epoch, "no-such-session"), CancellationToken.None)).Status);
            var children = await rpc.SessionChildrenAsync(new(Epoch, f.Parent.SessionId), CancellationToken.None);
            Assert.AreEqual("ok", children.Status);
            Assert.AreEqual((2, 4, false, false), (children.DirectCount, children.TotalCount, children.Truncated, children.RootAtWork));
            Assert.AreEqual(("global", "business", "Parent chat"), (children.Root!.ScopeKind, children.Root.SpaceId, children.Root.Title));
            // The tree read from the top, the oldest child first; each row says where it is.
            CollectionAssert.AreEqual(new[]
            {
                (f.ProjectChild.SessionId, f.Parent.SessionId, 1, "project", f.ProjectA.Id, "personal"),
                (f.ProjectGrandchild.SessionId, f.ProjectChild.SessionId, 2, "project", f.ProjectB.Id, "personal"),
                (f.ChatGrandchild.SessionId, f.ProjectChild.SessionId, 2, "global", (string?)null, "personal"),
                (f.ChatChild.SessionId, f.Parent.SessionId, 1, "global", null, "business"),
            }, children.Children.Select(child => (child.Session.Id, child.ParentSessionId, child.Depth, child.Session.ScopeKind, child.Session.ProjectId,
                child.Session.SpaceId)).ToArray());
            Assert.IsFalse(children.Children.Any(child => child.AtWork));
            CollectionAssert.AreEquivalent(children.Children.Select(child => child.Session.Id).ToArray(), children.DescendantIds.ToArray());
            Assert.AreEqual(children.TotalCount, JsonSerializer.Deserialize(JsonSerializer.Serialize(children, DesktopJsonContext.Default.WorkspaceSessionChildrenResponse),
                DesktopJsonContext.Default.WorkspaceSessionChildrenResponse)!.TotalCount);
            var grandchildren = await rpc.SessionChildrenAsync(new(Epoch, f.ProjectChild.SessionId), CancellationToken.None);
            Assert.AreEqual((2, 2), (grandchildren.DirectCount, grandchildren.TotalCount), "The children of a child are listed from it too.");

            // Delete alone still refuses a session with children: they are never orphaned.
            var title = children.Root.Title;
            Assert.AreEqual("has_children", (await rpc.DeleteSessionAsync(new(Epoch, "global", null, f.Global, f.Parent.SessionId, title), CancellationToken.None)).Status);

            WorkspaceDeleteSessionTreeRequest Tree(IReadOnlyList<string> descendants, string? confirmed = null)
                => new(Epoch, "global", null, f.Global, f.Parent.SessionId, confirmed ?? title, descendants);
            var shown = children.DescendantIds;
            Assert.AreEqual("invalid_scope", (await rpc.DeleteSessionTreeAsync(Tree(shown) with { Scope = "project" }, CancellationToken.None)).Status);
            Assert.AreEqual("session_missing", (await rpc.DeleteSessionTreeAsync(Tree(shown, "Another title"), CancellationToken.None)).Status);
            // A tree that is not the one the user saw is shown again, and nothing is deleted.
            Assert.AreEqual("descendants_changed", (await rpc.DeleteSessionTreeAsync(Tree([.. shown.Skip(1)]), CancellationToken.None)).Status);
            Assert.AreEqual("descendants_changed", (await rpc.DeleteSessionTreeAsync(Tree([.. shown, f.Unrelated.SessionId]), CancellationToken.None)).Status);
            Assert.AreEqual(7, (await new WorkspaceService(f.Global).SnapshotAsync(new(), CancellationToken.None)).Sessions.Length);

            var deleted = await rpc.DeleteSessionTreeAsync(Tree(shown), CancellationToken.None);
            Assert.AreEqual("ok", deleted.Status);
            Assert.AreEqual(f.Parent.SessionId, deleted.Deleted[^1], "The session goes last, after the sessions below it.");
            var order = deleted.Deleted.ToList();
            Assert.IsTrue(order.IndexOf(f.ProjectGrandchild.SessionId) < order.IndexOf(f.ProjectChild.SessionId));
            Assert.IsTrue(order.IndexOf(f.ChatGrandchild.SessionId) < order.IndexOf(f.ProjectChild.SessionId));
            Assert.AreEqual(5, deleted.Deleted.Count);
            Assert.AreEqual(deleted.Status, JsonSerializer.Deserialize(JsonSerializer.Serialize(deleted, DesktopJsonContext.Default.WorkspaceDeleteSessionTreeResponse),
                DesktopJsonContext.Default.WorkspaceDeleteSessionTreeResponse)!.Status);
            var fresh = await new WorkspaceService(f.Global).SnapshotAsync(new(), CancellationToken.None);
            CollectionAssert.AreEquivalent(new[] { f.Unrelated.SessionId, f.UnrelatedChat.SessionId }, fresh.Sessions.Select(session => session.Id).ToArray());
            Assert.AreEqual("source files stay", await File.ReadAllTextAsync(marker));
            Assert.IsNotNull(await host.ProjectCatalog.GetByIdAsync(f.ProjectA.Id));
            Assert.IsNotNull(await host.ProjectCatalog.GetByIdAsync(f.ProjectB.Id));
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(f.Root, recursive: true); }
    }

    [TestMethod]
    public async Task ADescendantThatWaitsForAnAnswer_IsShownAtWork_AndKeepsTheWholeTree()
    {
        var f = await CreateAsync();
        try
        {
            await using var host = f.Host;
            var execution = host.Commands.Asks.CreateExecution(Guid.NewGuid(), f.ChatGrandchild.SessionId, default);
            try
            {
                execution.Bind(Guid.NewGuid(), 1, Provider.ProviderId);
                await execution.StartedAsync(new("producer-run"), default);
                using var json = JsonDocument.Parse("{\"args\":[\"ask\",\"--stdin\"],\"stdin\":\"{\\\"questions\\\":[{\\\"title\\\":\\\"Question\\\",\\\"question\\\":\\\"Continue?\\\",\\\"freeform\\\":{}}]}\"}");
                Assert.IsTrue((await execution.Tool.Handler(new(Provider.ProviderId, f.ChatGrandchild.SessionId, "call", "alta", json.RootElement.Clone()), default)).Success);
            }
            finally { execution.Close(); }
            var rpc = new WorkspaceService(host, Epoch);
            var children = await rpc.SessionChildrenAsync(new(Epoch, f.Parent.SessionId), CancellationToken.None);
            CollectionAssert.AreEqual(new[] { f.ChatGrandchild.SessionId }, children.Children.Where(child => child.AtWork).Select(child => child.Session.Id).ToArray());

            var refused = await rpc.DeleteSessionTreeAsync(new(Epoch, "global", null, f.Global, f.Parent.SessionId, children.Root!.Title, children.DescendantIds),
                CancellationToken.None);
            Assert.AreEqual(("session_in_use", f.ChatGrandchild.SessionId, 0), (refused.Status, refused.BlockingSessionId, refused.Deleted.Count));
            Assert.AreEqual(7, (await new WorkspaceService(f.Global).SnapshotAsync(new(), CancellationToken.None)).Sessions.Length, "Nothing is deleted.");
            await rpc.CloseSessionsAsync();
            await rpc.CloseImportsAsync();
        }
        finally { Directory.Delete(f.Root, recursive: true); }
    }

    private sealed class DraftRuntime : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => Provider;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken token = default) => throw new NotSupportedException();
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken token = default)
            => Task.FromResult<IAgentSession>(new DraftSession(options.SessionId!, options.WorkingDirectory));
        public Task<IAgentSession> ResumeSessionAsync(string id, AgentSessionResumeOptions options, CancellationToken token = default)
            => Task.FromResult<IAgentSession>(new DraftSession(id, options.WorkingDirectory));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DraftSession(string id, string? path) : IAgentSession
    {
        public ModelProviderId ProviderId => Provider.ProviderId;
        public string SessionId => id;
        public string? WorkspacePath => path;
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; yield break; }
        public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();
        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken token = default) => throw new NotSupportedException();
        public Task AbortAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken token = default) => throw new NotSupportedException();
        public Task CompactAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
