using System.Collections.Frozen;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
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
    public void WithoutAServer_TheDirectoryIsThePairedMachines_WithTheirStateVersionAndSessionsAtWork()
    {
        var now = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var mini = new FleetPairedMachine("mini-id", "mini as paired", RemoteMachineState.Online, new RemoteListing(
            new FleetNodeInfo("Mac mini", "mini-id", "macos", "4.0.1", [], [], []) { Providers = [new FleetProviderInfo("codex", "Codex", true, null, [])] },
            [
                new FleetSessionSummary("s-works", "Fix the build", "p1", "default", null, "codex", "gpt-6", now, now.AddMinutes(5), "working"),
                new FleetSessionSummary("s-waits", "", null, "default", null, null, null, now, now.AddMinutes(1), "waiting"),
                new FleetSessionSummary("s-idle", "Old", null, "default", null, null, null, now, now, "idle"),
            ], now));
        var gone = new FleetPairedMachine("box-id", "build box", RemoteMachineState.Offline, RemoteListing.Empty);

        var directory = FleetServerService.DescribePaired([gone, mini]);

        Assert.AreEqual("paired", directory.Status);
        Assert.IsNull(directory.LinkId, "no server");
        CollectionAssert.AreEqual(new[] { "Mac mini", "build box" }, directory.Machines.Select(static machine => machine.Name).ToArray(),
            "online first, named as the machine says, else as it was paired");
        var online = directory.Machines[0];
        Assert.AreEqual((true, "online", 1, 3), (online.Online, online.Presence, online.ActiveSessions, online.Sessions), "one session at work of three");
        Assert.AreEqual(("4.0.1", "macos"), (online.Version, online.Os));
        Assert.AreEqual((true, true, false), (online.IsPaired, online.IsReachable, online.IsThisComputer));
        CollectionAssert.AreEqual(new[] { "Codex" }, online.Providers);
        var offline = directory.Machines[1];
        Assert.AreEqual((false, "offline", 0), (offline.Online, offline.Presence, offline.ActiveSessions));
        Assert.IsNull(offline.Version, "never reached since the start");
        CollectionAssert.AreEqual(new[] { "s-works:running", "s-waits:requires_action", "s-idle:idle" },
            directory.Sessions.Select(static session => $"{session.Id}:{session.State}").ToArray(), "the latest first, in the states of a server's directory");
        Assert.IsTrue(directory.Sessions.All(static session => session.CanOpen && session.MachineId == "mini-id"), "a session of a paired machine opens here");
        Assert.AreEqual("Fix the build", directory.Sessions[0].Title);
        Assert.IsNull(directory.Sessions[1].Title);
        Assert.AreEqual(("gpt-6", "codex"), (directory.Sessions[0].Model, directory.Sessions[0].ProviderType));
    }

    [TestMethod]
    public void PersonName_IsTheFullNameOfTheAccount_NeverItsBareName()
    {
        Assert.AreEqual("Alex Mutel", PersonName.FromPasswd("alex:x:1000:1000:Alex Mutel,,,:/home/alex:/bin/bash"));
        Assert.IsNull(PersonName.FromPasswd("alex:x:1000:1000::/home/alex:/bin/bash"), "no comment, no name");
        Assert.IsNull(PersonName.FromPasswd(null));
        Assert.AreEqual("Alex Mutel", PersonName.Clean("  Alex Mutel\n", "alex"));
        Assert.IsNull(PersonName.Clean("alex", "alex"), "the account name is not a full name");
        Assert.IsNull(PersonName.Clean("   ", "alex"));
        Assert.AreEqual(128, PersonName.Clean(new string('a', 300), "alex")!.Length);
        // Whatever the system says, reading it never throws.
        _ = PersonName.ReadOnce()();
    }

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
                PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory, MetadataKeys = options.MetadataKeys, MinimumBackoff = TimeSpan.FromMilliseconds(100),
                PublishDelay = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.FromMilliseconds(200), PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
            });
            var epoch = Guid.NewGuid().ToString("D");
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog, IsPaired = static _ => false, PersonName = static () => "Alex Example" };

            var none = await service.StatusAsync(new FleetServerRequest(epoch), token);
            Assert.AreEqual(("ok", 0), (none.Status, none.Servers.Length));
            Assert.AreEqual("Alex Example", none.PersonName, "the setup proposes the name of the person signed in as the owner's");
            Assert.AreEqual("no_server", service.Directory(new FleetServerDirectoryRequest(epoch)).Status);
            Assert.AreEqual("stale_epoch", (await service.StatusAsync(new FleetServerRequest("other"), token)).Status);
            Assert.AreEqual("invalid_url", (await service.CreateAsync(new FleetServerCreateRequest(epoch, "ftp://fleet", "x", "Alex"), token)).Status);
            // A mistyped word of a recovery key is said by its position, without the words.
            using (var generated = FleetRecoveryKey.Generate())
            {
                var typed = generated.Words.Split(' ');
                typed[2] = "zzzz";
                var mistyped = await service.RecoverAsync(new FleetServerRecoverRequest(epoch, address.ToString(), string.Join(' ', typed)), token);
                Assert.AreEqual(("recovery.unknown_word", "3"), (mistyped.Status, mistyped.Message));
            }

            var wrong = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), "WRONG-CODE", "Alex"), token);
            Assert.AreEqual("setup.invalid_code", wrong.Status, wrong.Message);
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            Assert.AreEqual(("Home", "approved", true), (created.Server!.Organization, created.Server.Link, created.Server.Controller));
            // The owner's recovery key comes with the server, its words given once to be shown, and it is in the signed list.
            Assert.AreEqual(32, created.RecoveryWords?.Split(' ').Length, created.Message);
            Assert.IsTrue(FleetRecoveryKey.TryParse(created.RecoveryWords, out var recoveryKey));
            string recoveryDeviceId;
            using (recoveryKey)
            using (var recoveryIdentity = recoveryKey.CreateIdentity())
            {
                recoveryDeviceId = recoveryIdentity.DeviceId;
            }

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
            Assert.IsFalse(devices.Devices.Any(static device => device.Kind == FleetDeviceKinds.Recovery), "the recovery key is no device to list");
            var mismatched = await service.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id, "acid acorn actor agent alarm album", pending.Kind), token);
            Assert.AreEqual(FleetDeviceListException.Code, mismatched.Status, "other words than the device's: nothing signed");
            Assert.AreEqual("invalid_request", (await service.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id, pending.Words), token)).Status,
                "an approval names the class the person approves");
            Assert.AreEqual(FleetDeviceListException.Code, (await service.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id, pending.Words,
                FleetDeviceKinds.Controller), token)).Status, "a machine is not signed in as a controller: nothing signed");
            var approved = await service.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id, pending.Words, pending.Kind), token);
            Assert.AreEqual(("ok", "approved"), (approved.Status, approved.Device?.Status), approved.Message);
            Assert.AreEqual(true, (await service.StatusAsync(new FleetServerRequest(epoch), token)).Servers.Single().RecoveryKey, "the card says the owner keeps a recovery key");
            Assert.AreEqual(FleetDeviceKinds.Recovery, servers.Connections.Single().Connector!.Devices.Current!.Members[recoveryDeviceId].Kind, "in the signed list");

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
            // Its title travels sealed with the key this computer made for its space; it opens here, and the server holds ciphertext only.
            Assert.AreEqual("A chat", session.Title);
            var connector = servers.Connections.Single().Connector!;
            StringAssert.Contains(connector.Directory.Sessions["chat-1"].Fact.SealedMetadata, "\"alg\":\"a256gcm\"");
            Assert.IsFalse(connector.Directory.Sessions["chat-1"].Fact.SealedMetadata!.Contains("A chat", StringComparison.Ordinal));
            // The approved node got its own copy of the key, which opens with its device key only.
            deadline = DateTime.UtcNow + Patience;
            CodeAlta.Fleet.Protocol.Api.MetadataKeyDto? nodeCopy;
            using (var node = new FleetServerClient(address, nodeKey))
            {
                while ((nodeCopy = (await node.ListMetadataKeysAsync(token)).SingleOrDefault())?.Wrapped is null)
                {
                    if (DateTime.UtcNow > deadline) Assert.Fail("The node did not get its copy of the metadata key.");
                    await Task.Delay(100, token);
                }
            }

            var nodeList = await new FleetOrganizationDevices(address, nodeKey).RefreshAsync(token);
            Assert.AreEqual(connector.MetadataKeys!.CurrentFor(null)!.Id, FleetKeyWrap.Unwrap(nodeCopy!.Wrapped!, nodeCopy.Id, nodeCopy.Epoch, nodeCopy.Space, nodeKey, nodeList).Id,
                "the key of the default space, the one linked");

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
            // The server withdrew what this computer published, and refuses it from then on.
            using (var node = new FleetServerClient(address, nodeKey))
            {
                Assert.IsFalse((await node.ListMachinesAsync(token)).Any(machine => machine.DeviceId == servers.DeviceId), "the inventory of this computer is gone");
                Assert.AreEqual("revoked", (await node.GetDeviceAsync(servers.DeviceId, token)).Status);
                Assert.IsTrue((await node.ListMetadataKeysAsync(token)).All(key => !key.WrappedTo.Contains(servers.DeviceId!)), "its copies of the keys are gone");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task TheOwnersComputerSetsTheTelemetryOfTheFleetWithItsHeadersMasked()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-telemetry-rpc-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("global"), CurrentProjectPath = At("project"), DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0");
            var epoch = Guid.NewGuid().ToString("D");
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog };
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;

            var none = await service.TelemetryAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual(("ok", false, "direct"), (none.Status, none.Telemetry!.Enabled, none.Telemetry.Route), "a fleet without a setting reads as off");

            const string Secret = "secret-api-key-0123456789";
            var set = await service.SetTelemetryAsync(new FleetTelemetryRequest(epoch, linkId, true, "https://otlp.example:4318/", FleetTelemetryProtocols.HttpProtobuf,
                FleetTelemetryRoutes.Server, [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Logs], [new FleetTelemetryHeaderEdit("x-api-key", Secret)], "production"), token);
            Assert.AreEqual("ok", set.Status, set.Message);
            var view = set.Telemetry!;
            Assert.AreEqual((true, "https://otlp.example:4318", "server", "production"), (view.Enabled, view.Endpoint, view.Route, view.Environment));
            CollectionAssert.AreEqual(new[] { "traces", "logs" }, view.Signals);
            Assert.AreEqual(("x-api-key", "\u2022\u2022\u2022\u2022"), (view.Headers.Single().Name, view.Headers.Single().Masked), "the page never sees a secret, nor its end");

            // A header sent without a value keeps the one the server holds.
            var again = await service.SetTelemetryAsync(new FleetTelemetryRequest(epoch, linkId, true, "https://otlp.example:4318", FleetTelemetryProtocols.HttpProtobuf,
                FleetTelemetryRoutes.Server, [FleetTelemetrySignals.Traces], [new FleetTelemetryHeaderEdit("x-api-key", null)], null), token);
            Assert.AreEqual("ok", again.Status, again.Message);
            var held = await servers.GetTelemetryAsync(linkId, token);
            Assert.AreEqual(string.Empty, held.ServerHeaders!["x-api-key"], "the server gives the names of its headers, never their values; it keeps the value");
            Assert.IsTrue(FleetTelemetrySettingsDocuments.TryParse(held.Settings!.Payload, out var signed));
            Assert.AreEqual((2L, (string?)null), (signed.Sequence, signed.Environment));
            Assert.IsFalse(held.Settings.Payload.Contains(Secret, StringComparison.Ordinal), "the headers of the server route are not in what the machines get");

            var refused = await service.SetTelemetryAsync(new FleetTelemetryRequest(epoch, linkId, true, "https://otlp.example:4317", FleetTelemetryProtocols.Grpc,
                FleetTelemetryRoutes.Server, [FleetTelemetrySignals.Traces], [], null), token);
            Assert.AreEqual("invalid_request", refused.Status, "the server forwards OTLP over HTTP only");
            var unknown = await service.SetTelemetryAsync(new FleetTelemetryRequest(epoch, linkId, true, "https://otlp.example:4318", FleetTelemetryProtocols.HttpProtobuf,
                FleetTelemetryRoutes.Direct, [FleetTelemetrySignals.Traces], [new FleetTelemetryHeaderEdit("x-other", null)], null), token);
            Assert.AreEqual("invalid_request", unknown.Status, "a header without a value must be one the server holds");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task TheOwnersComputerStartsAnUpdateOfTheFleetAndSetsItsPolicy()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-update-rpc-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            // A folder feed with both products at 4.0.2, for the runtime of this machine (the server's).
            var feed = At("feed");
            var runtime = CodeAlta.Fleet.Update.UpdateProduct.CurrentRuntime()!;
            foreach (var product in new[] { "CodeAlta.Node", "CodeAlta.Server" })
            {
                File.WriteAllText(Path.Combine(feed, $"{product}.4.0.2.nupkg"), "tool " + product);
                File.WriteAllText(Path.Combine(feed, $"{product}.{runtime}.4.0.2.nupkg"), "runtime " + product);
            }

            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
                UpdateStatus = static () => new FleetUpdateStatus("4.0.1", null, null, null, null, null, [], null), UpdateInterval = TimeSpan.FromHours(1),
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("global"), CurrentProjectPath = At("project"), DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0");
            var epoch = Guid.NewGuid().ToString("D");
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog };
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;

            var before = await service.UpdatesAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual(("ok", "4.0.1", "manual", 0), (before.Status, before.Updates!.ServerVersion, before.Updates.Policy.Mode, before.Updates.Campaigns.Length));

            var start = DateTimeOffset.UtcNow.AddDays(1);
            var started = await service.StartUpdateAsync(new FleetUpdateStartRequest(epoch, linkId, null, feed, start.ToString("O"), "Europe/Paris", null, false), token);
            Assert.AreEqual("ok", started.Status, started.Message);
            Assert.AreEqual(("4.0.2", "scheduled", "Europe/Paris", 1), (started.Campaign!.TargetVersion, started.Campaign.State, started.Campaign.TimeZone, started.Campaign.Sequence),
                "the latest version both products list, at the time chosen");
            Assert.AreEqual(start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"), DateTimeOffset.Parse(started.Campaign.NotBefore!).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"));

            var cancelled = await service.CancelUpdateAsync(new FleetUpdateCancelRequest(epoch, linkId, started.Campaign.Id), token);
            Assert.AreEqual(("ok", "cancelled"), (cancelled.Status, cancelled.Campaign!.State));

            var pinned = await service.SetUpdatePolicyAsync(new FleetUpdatePolicyRequest(epoch, linkId, "pinned", "4.0.1", null, null, null), token);
            Assert.AreEqual("ok", pinned.Status, pinned.Message);
            var after = await service.UpdatesAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual(("pinned", "4.0.1", "cancelled"), (after.Updates!.Policy.Mode, after.Updates.Policy.PinnedVersion, after.Updates.Campaigns[0].State));
            var refused = await service.StartUpdateAsync(new FleetUpdateStartRequest(epoch, linkId, "4.0.2", feed, null, null, null, false), token);
            Assert.AreEqual("update.conflict", refused.Status, "a pinned fleet stays on its version");
            var missing = await service.StartUpdateAsync(new FleetUpdateStartRequest(epoch, linkId, "4.0.9", feed, null, null, null, false), token);
            Assert.AreEqual("update.not_found", missing.Status, "the feed has no such version");
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
