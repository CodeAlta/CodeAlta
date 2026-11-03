using System.Collections.Frozen;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The <c>fleetServer</c> service of the window against a CodeAlta Server of this process and a host of its own: a server
/// created with its setup code, a space linked to it, a node enrolled with a token and approved with this desktop's key,
/// and the directory, where this computer and its published session show; then leaving the server.
/// </summary>
[TestClass]
public sealed class FleetServerRpcTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly ModelProviderId Provider = new("fleet-fixture");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CreateLinkApproveAndFollowTheDirectoryOfAServer()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-server-rpc-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { },
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            var global = At("global");
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = At("project"), DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            var chat = await AddSessionAsync(host, "chat-1", global, token);
            await AddTurnAsync(host, "chat-1", token);
            await AddSessionAsync(host, "private-1", global, token, isPrivate: true);

            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0", static options => new FleetConnectorOptions
            {
                ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
                PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory, MinimumBackoff = TimeSpan.FromMilliseconds(100),
                PublishDelay = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.FromMilliseconds(200), PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
            });
            var epoch = Guid.NewGuid().ToString("D");
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog, IsPaired = static _ => false };

            var none = await service.StatusAsync(new FleetServerRequest(epoch), token);
            Assert.AreEqual(("ok", 0), (none.Status, none.Servers.Length));
            Assert.AreEqual("no_server", service.Directory(new FleetServerDirectoryRequest(epoch)).Status);
            Assert.AreEqual("stale_epoch", (await service.StatusAsync(new FleetServerRequest("other"), token)).Status);
            Assert.AreEqual("invalid_url", (await service.CreateAsync(new FleetServerCreateRequest(epoch, "ftp://fleet", "x", "Alex"), token)).Status);

            var wrong = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), "WRONG-CODE", "Alex"), token);
            Assert.AreEqual("setup.invalid_code", wrong.Status, wrong.Message);
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            Assert.AreEqual(("Home", "approved", true), (created.Server!.Organization, created.Server.Link, created.Server.Controller));
            var linkId = created.Server.Id;
            Assert.AreEqual("already_linked", (await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), "x", "Alex"), token)).Status);

            Assert.AreEqual("ok", (await service.LinkSpaceAsync(new FleetServerSpaceRequest(epoch, SpaceDescriptor.DefaultId, linkId), token)).Status);
            var status = await service.StatusAsync(new FleetServerRequest(epoch), token);
            CollectionAssert.AreEqual(new[] { SpaceDescriptor.DefaultId }, status.Servers.Single().Spaces);
            Assert.IsNotNull(status.Words);

            // A machine enrolled with a token of this desktop waits, then this desktop approves it with its own key.
            var made = await service.CreateTokenAsync(new FleetServerTokenRequest(epoch, linkId, 2, "Mac mini"), token);
            Assert.AreEqual("ok", made.Status, made.Message);
            using var nodeKey = DeviceIdentity.LoadOrCreate(At("node-fleet"));
            using (var node = new FleetServerClient(address, nodeKey)) await node.EnrollAsync(made.Token!, "Mac mini", token);
            var devices = await service.DevicesAsync(new FleetServerLinkRequest(epoch, linkId), token);
            var pending = devices.Devices.First();
            Assert.AreEqual(("Mac mini", "pending", nodeKey.Fingerprint.ToShortWords()), (pending.Name, pending.Status, pending.Words), "the pending device comes first, with the words of its key");
            var approved = await service.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id), token);
            Assert.AreEqual(("ok", "approved"), (approved.Status, approved.Device?.Status), approved.Message);

            // The control connection publishes this computer and its session of the linked space, never the Private one.
            var deadline = DateTime.UtcNow + Patience;
            FleetDirectoryResult directory;
            while (true)
            {
                directory = service.Directory(new FleetServerDirectoryRequest(epoch));
                if (directory.Machines.Any(static machine => machine.IsThisComputer && machine.Online) && directory.Sessions.Any(static session => session.Id == "chat-1")) break;
                if (DateTime.UtcNow > deadline) Assert.Fail($"The directory did not show this computer and its session: {directory.Connection}.");
                await Task.Delay(100, token);
            }

            Assert.AreEqual(("online", "Home"), (directory.Connection, directory.Organization));
            var session = directory.Sessions.Single(static session => session.Id == "chat-1");
            Assert.IsTrue(session.CanOpen, "a session of this computer opens here");
            Assert.AreEqual(FleetSessionStates.Idle, session.State);
            Assert.IsFalse(directory.Sessions.Any(static session => session.Id == "private-1"), "a Private session is never published");
            Assert.AreEqual("Desktop", directory.Machines.Single(static machine => machine.IsThisComputer).Name);
            // Its counts come from its journal: one turn with one tool call, the tokens of its usage.
            Assert.AreEqual((1, 1500), (session.Turns, session.Tokens));
            var fact = servers.Connections.Single().Connector!.Directory.Sessions["chat-1"].Fact;
            Assert.AreEqual(new FleetSessionCounts(1, 1, 1200, 300), fact.Counts);

            // Made Private, the session is withdrawn from the server and leaves the directory.
            await host.SessionViewCatalog.JournalStore.AppendStateAsync(chat, new SessionViewLocalState { Options = new AgentSessionOptions { Private = true } },
                token);
            await host.AgentSessionCatalog.InvalidateAsync(token);
            deadline = DateTime.UtcNow + Patience;
            while (service.Directory(new FleetServerDirectoryRequest(epoch)).Sessions.Any(static session => session.Id == "chat-1"))
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("The Private session was not withdrawn.");
                await Task.Delay(100, token);
            }

            Assert.AreEqual("ok", (await service.LeaveAsync(new FleetServerLinkRequest(epoch, linkId), token)).Status);
            Assert.AreEqual(0, (await service.StatusAsync(new FleetServerRequest(epoch), token)).Servers.Length);
            Assert.IsNull((await host.SpaceCatalog.GetAsync(SpaceDescriptor.DefaultId, token))!.Settings.Fleet, "the space is unlinked with the server");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static SessionViewDescriptor Descriptor(string id, string path, DateTimeOffset? created = null) => new()
    {
        SessionId = id, Kind = SessionViewKind.GlobalSession, ProviderId = Provider.Value, ProviderKey = Provider.Value, WorkingDirectory = path, Title = "A chat",
        CreatedAt = created ?? DateTimeOffset.UtcNow, UpdatedAt = created ?? DateTimeOffset.UtcNow, SpaceId = SpaceDescriptor.DefaultId,
    };

    // One turn: a prompt, a tool call and the usage of one request to the model.
    private static async Task AddTurnAsync(CodeAltaHost host, string id, CancellationToken token)
    {
        var at = DateTimeOffset.UtcNow;
        var run = new AgentRunId("run-1");
        await host.SessionViewCatalog.JournalStore.CreateSessionStore().AppendEventsAsync(Provider.Value, Provider.Value, id, [
            new AgentContentCompletedEvent(Provider, id, at, run, AgentContentKind.User, "prompt:1", null, "Hello"),
            new AgentActivityEvent(Provider, id, at, run, AgentActivityKind.ToolCall, AgentActivityPhase.Started, "tool:1", null, "read_file", null),
            new AgentSessionUpdateEvent(Provider, id, at, run, AgentSessionUpdateKind.UsageUpdated, null,
                Usage: new(LastOperation: new AgentOperationUsageSnapshot(InputTokens: 1200, OutputTokens: 300))),
            new AgentSessionUpdateEvent(Provider, id, at, run, AgentSessionUpdateKind.Idle, null),
        ], token);
    }

    private static async Task<SessionViewDescriptor> AddSessionAsync(CodeAltaHost host, string id, string path, CancellationToken token, bool isPrivate = false)
    {
        var created = DateTimeOffset.UtcNow;
        var descriptor = Descriptor(id, path, created);
        await host.SessionViewCatalog.JournalStore.EnsureHeaderAsync(descriptor, token);
        // The options of a session are a record of its local state, as the window writes them when it creates one.
        if (isPrivate) await host.SessionViewCatalog.JournalStore.AppendStateAsync(descriptor, new SessionViewLocalState { Options = new AgentSessionOptions { Private = true } }, token);
        await host.SessionViewCatalog.JournalStore.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
        {
            SessionId = id, ProviderId = Provider, ProtocolFamily = Provider.Value, ProviderKey = Provider.Value, WorkingDirectory = path, Title = "A chat",
            CreatedAt = created, UpdatedAt = created,
        }, token);
        await host.AgentSessionCatalog.InvalidateAsync(token);
        return descriptor;
    }
}
