using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceCreateSessionTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";
    private static readonly ModelProviderDescriptor Provider = new(new("desktop-draft-fixture"), "Draft fixture")
        { IsDefault = true, DefaultModelId = "fixture-model" };

    [TestMethod]
    public async Task ExplicitProviderIsExactEnabledAndNeverFallsBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-provider-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            var alternate = new ModelProviderDescriptor(new("alternate"), "Alternate");
            providers.RegisterOrReplace(Provider, () => throw new AssertFailedException("Descriptor admission must not initialize providers."));
            providers.RegisterOrReplace(alternate, () => throw new AssertFailedException("Descriptor admission must not initialize providers."));
            var admitted = new List<ModelProviderDescriptor>();
            var service = new WorkspaceService(reads, catalog, Epoch, providers, (_, provider, _) =>
            {
                admitted.Add(provider);
                return Task.FromResult(new SessionViewDescriptor { SessionId = "created", Kind = SessionViewKind.GlobalSession,
                    WorkingDirectory = root });
            });
            // Deserialize the new optional wire field so the regression runs against the old implementation.
            WorkspaceCreateSessionRequest Request(string? id) => JsonSerializer.Deserialize(
                JsonSerializer.Serialize(new { expectedHostEpoch = Epoch, scope = "global", providerId = id }),
                DesktopJsonContext.Default.WorkspaceCreateSessionRequest)!;
            var result = await service.CreateSessionAsync(Request("alternate"), CancellationToken.None);
            Assert.AreEqual("ok", result.Status);
            Assert.AreSame(alternate, admitted.Single());
            foreach (var id in new[] { " alternate", "alternate ", "", "bad\nvalue", new string('x', 257) })
                Assert.AreEqual("invalid_scope", (await service.CreateSessionAsync(Request(id), CancellationToken.None)).Status, id);
            foreach (var id in new[] { "Alternate", "absent" })
                Assert.AreEqual("provider_unavailable", (await service.CreateSessionAsync(Request(id), CancellationToken.None)).Status, id);
            providers.RegisterOrReplace(alternate with { IsEnabled = false }, () => throw new AssertFailedException());
            Assert.AreEqual("provider_unavailable", (await service.CreateSessionAsync(Request("alternate"), CancellationToken.None)).Status);
            providers.Unregister(alternate.ProviderId);
            Assert.AreEqual("provider_unavailable", (await service.CreateSessionAsync(Request("alternate"), CancellationToken.None)).Status);
            Assert.HasCount(1, admitted);
            Assert.AreEqual("ok", (await service.CreateSessionAsync(Request(null), CancellationToken.None)).Status);
            Assert.AreSame(Provider, admitted[1]);
            providers.RegisterOrReplace(Provider with { IsEnabled = false }, () => throw new AssertFailedException());
            providers.RegisterOrReplace(alternate, () => throw new AssertFailedException());
            Assert.AreEqual("ok", (await service.CreateSessionAsync(Request(null), CancellationToken.None)).Status);
            Assert.AreSame(alternate, admitted[2], "Without an enabled default the first enabled descriptor remains the default route.");
            await service.CloseSessionsAsync(); await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ArchiveAndCreationReserveSameOwnerThroughCanceledWaitAndDrain(bool archiveFirst)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-archive-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            var project = await catalog.UpsertFromPathAsync(root);
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            providers.RegisterOrReplace(Provider, () => new DraftRuntime());
            var calls = 0;
            var rpc = new WorkspaceService(reads, catalog, Epoch, providers, async (p, _, _) =>
            {
                calls++; entered.TrySetResult(); await release.Task;
                await catalog.EnsurePersistedAsync(p!);
                return new SessionViewDescriptor { SessionId = "created", Kind = SessionViewKind.ProjectSession,
                    ProjectRef = p!.Id, WorkingDirectory = p.ProjectPath };
            });
            rpc.ArchiveWriter = async (request, revision) =>
            {
                entered.TrySetResult(); await release.Task;
                return await catalog.SetArchivedAsync(request.ProjectId, request.ProjectPath, request.SourcePath!, revision,
                    request.ExpectedArchived, request.Archived);
            };
            var evidence = await catalog.ReadArchiveAsync(project.Id, root);
            Assert.IsNotNull(evidence, "Ordinary serializer-produced projects must support archive.");
            var archive = new WorkspaceArchiveProjectRequest(Epoch, project.Id, root, false, true, true, evidence.SourcePath, evidence.Revision.ContentHash);
            var create = new WorkspaceCreateSessionRequest(Epoch, "project", project.Id, root, null, Provider.ProviderId.Value);
            using var cancel = new CancellationTokenSource();
            Task original = archiveFirst ? rpc.ArchiveProjectAsync(archive, cancel.Token) : rpc.CreateSessionAsync(create, cancel.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancel.Cancel();
                try { await original; Assert.Fail("Canceled wait must stop waiting."); } catch (OperationCanceledException) { }
                Assert.AreEqual("busy", (await rpc.ArchiveProjectAsync(archive, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3))).Status);
                Assert.AreEqual("busy", (await rpc.CreateSessionAsync(create, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3))).Status);
                Assert.AreEqual("busy", (await rpc.OpenProjectAsync(new(Epoch, root, true), CancellationToken.None)).Status);
                Assert.AreEqual("busy", (await rpc.RenameProjectAsync(new(Epoch, project.Id, root, evidence.SourcePath,
                    evidence.Revision.ContentHash!, "Renamed"), CancellationToken.None)).Status);
                var drain = Task.WhenAll(rpc.CloseImportsAsync(), rpc.CloseSessionsAsync());
                Assert.IsFalse(drain.IsCompleted);
                release.TrySetResult();
                await drain.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(archiveFirst, (await catalog.GetByIdAsync(project.Id))!.Archived);
                Assert.AreEqual(archiveFirst ? 0 : 1, calls);
                if (archiveFirst)
                {
                    var fresh = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) => throw new AssertFailedException("Archived create must refuse."));
                    Assert.AreEqual("project_missing", (await fresh.CreateSessionAsync(create, CancellationToken.None)).Status);
                    Assert.AreEqual("archived", (await fresh.OpenProjectAsync(new(Epoch, root, true), CancellationToken.None)).Status);
                    Assert.IsTrue((await catalog.GetByIdAsync(project.Id))!.Archived);
                    await fresh.CloseSessionsAsync(); await fresh.CloseImportsAsync();
                }
                Assert.AreEqual("closed", (await rpc.ArchiveProjectAsync(archive, CancellationToken.None)).Status);
                Assert.AreEqual("closed", (await rpc.CreateSessionAsync(create, CancellationToken.None)).Status);
            }
            finally { release.TrySetResult(); await rpc.CloseSessionsAsync(); await rpc.CloseImportsAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealOwnedHostCreatesProjectAndGlobalSessionsVisibleInActualCatalog(bool explicitProvider)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-create-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Test root already exists.");
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home"); var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            var alternate = new ModelProviderDescriptor(new("alternate"), "Alternate");
            var activity = new DraftActivity();
            activity.ReleaseCreate.SetResult();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => {
                    registry.RegisterOrReplace(Provider, () => new DraftRuntime());
                    registry.RegisterOrReplace(alternate, () => new DraftRuntime(activity, alternate));
                },
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var service = new WorkspaceService(host, Epoch);
            var projectRequest = new WorkspaceCreateSessionRequest(Epoch, "project", project.Id, projectPath, "Project draft",
                explicitProvider ? alternate.ProviderId.Value : null);
            Assert.AreEqual("project_missing", (await service.CreateSessionAsync(projectRequest with { ProjectPath = global }, CancellationToken.None)).Status);
            Assert.AreEqual(projectRequest, JsonSerializer.Deserialize(
                JsonSerializer.Serialize(projectRequest, DesktopJsonContext.Default.WorkspaceCreateSessionRequest),
                DesktopJsonContext.Default.WorkspaceCreateSessionRequest));
            var created = await service.CreateSessionAsync(projectRequest, CancellationToken.None);
            Assert.AreEqual("ok", created.Status);
            Assert.AreEqual(projectRequest.ProviderId, created.ProviderId);
            Assert.AreEqual(explicitProvider ? 1 : 0, activity.Creates, "Explicit choice must reach the real runtime creation callback.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(created.SessionId));
            Assert.AreEqual(projectPath, created.WorkspacePath);
            Assert.AreEqual(created, JsonSerializer.Deserialize(
                JsonSerializer.Serialize(created, DesktopJsonContext.Default.WorkspaceCreateSessionResponse),
                DesktopJsonContext.Default.WorkspaceCreateSessionResponse));
            var globalCreated = await service.CreateSessionAsync(new(Epoch, "global", null, null, null), CancellationToken.None);
            Assert.AreEqual("ok", globalCreated.Status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(globalCreated.SessionId));
            Assert.AreNotEqual(created.SessionId, globalCreated.SessionId);
            Assert.AreEqual(global, globalCreated.WorkspacePath);
            var snapshot = await service.SnapshotAsync(new(), CancellationToken.None);
            Assert.IsTrue(snapshot.Sessions.Any(s => s.Id == created.SessionId && s.WorkspacePath == projectPath));
            Assert.AreEqual(explicitProvider ? alternate.ProviderId.Value : Provider.ProviderId.Value,
                snapshot.Sessions.Single(s => s.Id == created.SessionId).ProviderKey);
            Assert.IsTrue(snapshot.Sessions.Any(s => s.Id == globalCreated.SessionId && s.WorkspacePath == global));
            Assert.IsNull(snapshot.Sessions.Single(s => s.Id == globalCreated.SessionId).SpaceId, "A page that names no space creates a session that records none.");

            // The page names the space it shows: the session belongs to it, and a space that is gone creates nothing.
            Assert.IsTrue((await host.SpaceCatalog.CreateAsync("Business")).Succeeded);
            var inSpace = await service.CreateSessionAsync(new(Epoch, "global", null, null, "In a space", SpaceId: "business"), CancellationToken.None);
            Assert.AreEqual("ok", inSpace.Status);
            Assert.AreEqual("business", (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions.Single(s => s.Id == inSpace.SessionId).SpaceId);
            Assert.AreEqual("business", (await host.RuntimeService.GetSessionSpaceAsync(inSpace.SessionId!))!.SpaceId);
            Assert.AreEqual("space_missing", (await service.CreateSessionAsync(new(Epoch, "global", null, null, null, SpaceId: "gone"), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await service.CreateSessionAsync(new(Epoch, "global", null, null, null, SpaceId: "Not An Id"), CancellationToken.None)).Status);
            var sessionsBefore = (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions.Length;
            Assert.AreEqual(3, sessionsBefore, "A refused space creates no session.");
            Assert.AreEqual(project.Id, (await host.RuntimeService.ResolveOwnedSessionAsync(created.SessionId!, CancellationToken.None))?.ProjectRef);
            Assert.AreEqual(SessionViewKind.GlobalSession,
                (await host.RuntimeService.ResolveOwnedSessionAsync(globalCreated.SessionId!, CancellationToken.None))?.Kind);
            Assert.HasCount(1, await host.ProjectCatalog.LoadAsync());
            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
            Assert.AreEqual("closed", (await service.CreateSessionAsync(projectRequest, CancellationToken.None)).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ASessionIsCreatedInThePermissionModeChosenBeforeItExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-create-permission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var home = Path.Combine(root, "home"); var builtin = Path.Combine(root, "builtin");
            var projectPath = Path.Combine(root, "project");
            foreach (var path in new[] { global, home, builtin, projectPath }) Directory.CreateDirectory(path);
            // A provider with modes of its own, as Claude Code has, configured with one of them.
            var cli = new ModelProviderDescriptor(new("cli-fixture"), "CLI fixture")
                { PermissionModes = ["default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions"], DefaultPermissionMode = "acceptEdits" };
            var activity = new DraftActivity();
            activity.ReleaseCreate.SetResult();
            var options = new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin, SessionPermissionModes = true,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry =>
                {
                    registry.RegisterOrReplace(Provider, () => new DraftRuntime());
                    registry.RegisterOrReplace(cli, () => new DraftRuntime(activity, cli));
                },
            };
            string sessionId;
            var host = await CodeAltaHost.CreateAsync(options);
            try
            {
                var service = new WorkspaceService(host, Epoch);
                // The composer of a new session reads the modes as an existing session of that provider offers them.
                var modes = service.DraftPermissionModes(new(Epoch, cli.ProviderId.Value));
                Assert.AreEqual("ok", modes.Status);
                CollectionAssert.AreEqual(new[] { "default", "acceptEdits", "auto", "dontAsk", "bypassPermissions" }, modes.PermissionModes.Select(mode => mode.Id).ToArray(),
                    "Plan stays a mode of the provider's configuration.");
                Assert.AreEqual("acceptEdits", modes.DefaultPermissionMode);
                // A provider without modes of its own offers those of the host.
                CollectionAssert.AreEqual(new[] { "default", "acceptEdits", "bypassPermissions" },
                    service.DraftPermissionModes(new(Epoch, Provider.ProviderId.Value)).PermissionModes.Select(mode => mode.Id).ToArray());
                Assert.AreEqual("provider_unavailable", service.DraftPermissionModes(new(Epoch, "absent")).Status);
                Assert.AreEqual("stale_epoch", service.DraftPermissionModes(new("22222222-2222-4222-8222-222222222222", cli.ProviderId.Value)).Status);
                Assert.AreEqual("invalid_scope", service.DraftPermissionModes(new(Epoch, " cli-fixture")).Status);

                // A mode the provider does not offer creates nothing.
                var request = new WorkspaceCreateSessionRequest(Epoch, "global", null, null, "Bypass", cli.ProviderId.Value, PermissionMode: "bypassPermissions");
                foreach (var refused in new[] { "plan", "unknown" })
                    Assert.AreEqual("invalid_permission_mode", (await service.CreateSessionAsync(request with { PermissionMode = refused }, CancellationToken.None)).Status, refused);
                Assert.AreEqual("invalid_permission_mode", (await service.CreateSessionAsync(request with { ProviderId = Provider.ProviderId.Value, PermissionMode = "auto" }, CancellationToken.None)).Status,
                    "A mode of another provider is not one of this provider.");
                Assert.AreEqual("invalid_scope", (await service.CreateSessionAsync(request with { PermissionMode = " auto" }, CancellationToken.None)).Status);
                Assert.AreEqual(0, activity.Creates);
                Assert.IsEmpty((await service.SnapshotAsync(new(), CancellationToken.None)).Sessions, "A refused mode creates no session.");
                Assert.AreEqual(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request, DesktopJsonContext.Default.WorkspaceCreateSessionRequest),
                    DesktopJsonContext.Default.WorkspaceCreateSessionRequest));

                // The session is created in the mode chosen: its provider starts it in that mode, and its choices name it as its own.
                var created = await service.CreateSessionAsync(request, CancellationToken.None);
                Assert.AreEqual("ok", created.Status);
                sessionId = created.SessionId!;
                Assert.AreEqual("bypassPermissions", activity.PermissionMode, "The provider session is created in the mode chosen.");
                Assert.AreEqual("bypassPermissions", (await host.Commands.GetObservedSelectionChoicesAsync(sessionId))!.Current.PermissionMode);
                // The host answers the requests of its first turn by that mode.
                Assert.AreEqual(SessionPermissionPolicy.Approve, host.RuntimeService.GetPermissionPolicy(sessionId));
                // Without a mode chosen, the session runs in the one of its provider and has none of its own.
                var plain = await service.CreateSessionAsync(request with { Title = "Plain", PermissionMode = null }, CancellationToken.None);
                Assert.AreEqual("ok", plain.Status);
                Assert.IsNull((await host.Commands.GetObservedSelectionChoicesAsync(plain.SessionId!))!.Current.PermissionMode);
                Assert.AreEqual(SessionPermissionPolicy.AcceptEdits, host.RuntimeService.GetPermissionPolicy(plain.SessionId!), "The mode the provider is configured with.");
                await service.CloseSessionsAsync();
                await service.CloseImportsAsync();
            }
            finally { await host.DisposeAsync(); }

            // The mode is saved with the session, as one chosen after it exists is.
            await using var reopened = await CodeAltaHost.CreateAsync(options);
            Assert.AreEqual("bypassPermissions", (await reopened.RuntimeService.ResolveOwnedSessionAsync(sessionId, CancellationToken.None))!.PermissionMode);

            // A host without command owner offers no mode, and refuses one.
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "bare") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            providers.RegisterOrReplace(Provider, () => throw new AssertFailedException("Descriptor admission must not initialize providers."));
            var bare = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) => throw new AssertFailedException("A refused mode creates nothing."));
            Assert.AreEqual("unconfigured", bare.DraftPermissionModes(new(Epoch, Provider.ProviderId.Value)).Status);
            Assert.AreEqual("invalid_permission_mode", (await bare.CreateSessionAsync(new(Epoch, "global", null, null, null, PermissionMode: "default"), CancellationToken.None)).Status);
            await bare.CloseSessionsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ASessionTakesOnlyTheProvidersOfItsSpace()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-provider-spaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var home = Path.Combine(root, "home"); var builtin = Path.Combine(root, "builtin");
            var projectPath = Path.Combine(root, "project");
            foreach (var path in new[] { global, home, builtin, projectPath }) Directory.CreateDirectory(path);
            // The provider the configuration makes the default is assigned to Business alone.
            File.WriteAllText(Path.Combine(global, "config.toml"), """
                [chat]
                default_provider = "business-only"

                [providers.business-only]
                type = "openai-chat"
                api_url = "http://127.0.0.1:9/v1"
                api_key = "fixture"
                spaces = ["business"]
                """);
            var businessOnly = new ModelProviderDescriptor(new("business-only"), "Business only") { IsDefault = true, DefaultModelId = "fixture-model" };
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry =>
                {
                    registry.RegisterOrReplace(Provider, () => new DraftRuntime());
                    registry.RegisterOrReplace(businessOnly, () => new DraftRuntime(null, businessOnly));
                },
            });
            Assert.IsTrue((await host.SpaceCatalog.CreateAsync("Business")).Succeeded);
            var service = new WorkspaceService(host, Epoch);

            // Without a provider named, a session starts with the default of its space.
            var inDefault = await service.CreateSessionAsync(new(Epoch, "global", null, null, "Default chat"), CancellationToken.None);
            var inBusiness = await service.CreateSessionAsync(new(Epoch, "global", null, null, "Business chat", SpaceId: "business"), CancellationToken.None);
            Assert.AreEqual(("ok", "ok"), (inDefault.Status, inBusiness.Status));
            var sessions = (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions;
            Assert.AreEqual(Provider.ProviderId.Value, sessions.Single(s => s.Id == inDefault.SessionId).ProviderKey, "The configured default is not in the default space.");
            Assert.AreEqual("business-only", sessions.Single(s => s.Id == inBusiness.SessionId).ProviderKey);

            // A provider named outside the space of the session is refused, and creates nothing.
            Assert.AreEqual(SessionRuntimeService.ProviderOutsideSpace,
                (await service.CreateSessionAsync(new(Epoch, "global", null, null, null, "business-only"), CancellationToken.None)).Status);
            Assert.AreEqual("ok", (await service.CreateSessionAsync(new(Epoch, "global", null, null, null, Provider.ProviderId.Value, SpaceId: "business"),
                CancellationToken.None)).Status, "A provider of no space is in every space.");
            Assert.AreEqual(3, (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions.Length);

            // The choices of a session and its switch follow its space.
            var choices = await host.Commands.GetProviderSelectionAsync(inDefault.SessionId!);
            CollectionAssert.AreEqual(new[] { Provider.ProviderId.Value }, choices!.Providers.Select(static p => p.ProviderId.Value).ToArray());
            Assert.AreEqual(SessionRuntimeService.ProviderOutsideSpace, await host.Commands.SelectProviderAsync(choices, "business-only"));
            var businessChoices = await host.Commands.GetProviderSelectionAsync(inBusiness.SessionId!);
            CollectionAssert.AreEquivalent(new[] { Provider.ProviderId.Value, "business-only" }, businessChoices!.Providers.Select(static p => p.ProviderId.Value).ToArray());
            Assert.IsTrue(await host.RuntimeService.IsSessionProviderInSpaceAsync(inBusiness.SessionId!));
            // What a session can use names the providers of its space.
            CollectionAssert.AreEqual(new[] { "Business only", "Draft fixture" },
                (await new DesktopSessionResources(host, home).ReadAsync(inBusiness.SessionId!, CancellationToken.None))!.Providers.ToArray());
            CollectionAssert.AreEqual(new[] { "Draft fixture" },
                (await new DesktopSessionResources(host, home).ReadAsync(inDefault.SessionId!, CancellationToken.None))!.Providers.ToArray());

            // A provider taken out of the space of a session leaves it without a provider to send with.
            new CodeAltaConfigStore(host.CatalogOptions).SaveGlobalSpaceAssignment(SpaceResourceKind.Provider, "business-only", ["personal"]);
            Assert.IsFalse(await host.RuntimeService.IsSessionProviderInSpaceAsync(inBusiness.SessionId!));
            Assert.IsTrue(await host.RuntimeService.IsSessionProviderInSpaceAsync(inDefault.SessionId!));
            // The composer names the provider by its display name and counts only the providers of the space.
            var outside = await host.Commands.GetSessionSpaceProvidersAsync(inBusiness.SessionId!);
            Assert.AreEqual((false, "Business only"), (outside!.ProviderInSpace, outside.ProviderName));
            CollectionAssert.AreEqual(new[] { Provider.ProviderId.Value }, outside.Providers.Select(static p => p.ProviderId.Value).ToArray());
            var operations = new SessionOperationsService(host.Commands, Epoch);
            var read = await operations.Choices(new(Epoch, inBusiness.SessionId!), CancellationToken.None);
            Assert.AreEqual(("ok", true, "Business only"), (read.Status, read.ProviderOutsideSpace, read.ProviderName));
            CollectionAssert.AreEqual(new[] { Provider.ProviderId.Value }, read.SpaceProviders!.ToArray());
            var refused = host.Commands.AdmitSend(new OwnedTextSendRequest(Guid.NewGuid().ToString("N"), inBusiness.SessionId!, "hello"));
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, refused.Kind);
            var outcome = await refused.Receipt!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual((OwnedSessionCommandOutcome.Failed, SessionRuntimeService.ProviderOutsideSpace), (outcome.Outcome, outcome.Code),
                "The session sends nothing until it switches to a provider of its space.");
            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ASessionWhoseProviderWasRemoved_KeepsItsChoices_AndSwitchesToTheDefaultProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-provider-removed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var home = Path.Combine(root, "home"); var builtin = Path.Combine(root, "builtin");
            var projectPath = Path.Combine(root, "project");
            foreach (var path in new[] { global, home, builtin, projectPath }) Directory.CreateDirectory(path);
            var config = Path.Combine(global, "config.toml");
            const string Chat = """
                [chat]
                default_provider = "desktop-draft-fixture"

                """;
            const string Gone = """
                [providers.gone]
                type = "openai-chat"
                api_url = "http://127.0.0.1:9/v1"
                api_key = "fixture"
                """;
            File.WriteAllText(config, Chat + Gone);
            var gone = new ModelProviderDescriptor(new("gone"), "Gone") { DefaultModelId = "fixture-model" };
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry =>
                {
                    registry.RegisterOrReplace(Provider, () => new DraftRuntime());
                    registry.RegisterOrReplace(gone, () => new DraftRuntime(null, gone));
                },
            });
            var service = new WorkspaceService(host, Epoch);
            var created = await service.CreateSessionAsync(new(Epoch, "global", null, null, "Gone chat", "gone"), CancellationToken.None);
            var fresh = await service.CreateSessionAsync(new(Epoch, "global", null, null, "Never sent", "gone"), CancellationToken.None);
            Assert.AreEqual(("ok", "ok"), (created.Status, fresh.Status));
            // What a session of a provider writes once it ran, here as CodeAlta 1.8 wrote it: the key of the provider as
            // the family of the summary, which only the provider's runtime, or its state, confirms.
            var sessionStore = host.SessionViewCatalog.JournalStore.CreateSessionStore();
            var summary = await sessionStore.GetSessionSummaryAsync(created.SessionId!, CancellationToken.None);
            await sessionStore.UpsertSessionAsync(summary! with { ProtocolFamily = "gone" }, CancellationToken.None);
            await sessionStore.UpsertStateAsync(new AgentSessionState
            {
                SessionId = created.SessionId!, ProviderKey = "gone", ProtocolFamily = "openai-chat", UpdatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
            var operations = new SessionOperationsService(host.Commands, Epoch);
            var read = await operations.Choices(new(Epoch, created.SessionId!), CancellationToken.None);
            Assert.AreEqual(("ok", null, null), (read.Status, read.ProviderUnavailable, read.ReplacementProvider));

            // Turned off, the provider is still the user's: the session names it, and is switched by hand only.
            File.WriteAllText(config, Chat + Gone.Replace("[providers.gone]", "[providers.gone]\nenabled = false", StringComparison.Ordinal));
            host.ModelProviderRegistry.Unregister(gone.ProviderId);
            read = await operations.Choices(new(Epoch, created.SessionId!), CancellationToken.None);
            Assert.AreEqual(("ok", "gone", true, null), (read.Status, read.Current?.ProviderKey, read.ProviderUnavailable, read.ReplacementProvider));

            // Removed, it is replaced by the default provider of the configuration.
            File.WriteAllText(config, Chat);
            read = await operations.Choices(new(Epoch, created.SessionId!), CancellationToken.None);
            Assert.AreEqual(("ok", true, Provider.ProviderId.Value), (read.Status, read.ProviderUnavailable, read.ReplacementProvider));
            Assert.AreEqual(0, read.Models.Count);
            var choices = await host.Commands.GetProviderSelectionAsync(created.SessionId!);
            Assert.AreEqual("gone", choices!.ProviderKey);
            CollectionAssert.AreEqual(new[] { Provider.ProviderId.Value }, choices.Providers.Select(static p => p.ProviderId.Value).ToArray());
            Assert.AreEqual("ok", await host.Commands.SelectProviderAsync(choices, Provider.ProviderId.Value));
            read = await operations.Choices(new(Epoch, created.SessionId!), CancellationToken.None);
            Assert.AreEqual(("ok", Provider.ProviderId.Value, null, null), (read.Status, read.Current?.ProviderKey, read.ProviderUnavailable, read.ReplacementProvider));

            // A session that never ran has no provider state: it switches all the same, with nothing to continue.
            Assert.IsNull(await sessionStore.GetStateAsync(fresh.SessionId!, CancellationToken.None));
            var freshChoices = await host.Commands.GetProviderSelectionAsync(fresh.SessionId!);
            Assert.AreEqual("ok", await host.Commands.SelectProviderAsync(freshChoices!, Provider.ProviderId.Value));
            Assert.AreEqual(Provider.ProviderId.Value, (await operations.Choices(new(Epoch, fresh.SessionId!), CancellationToken.None)).Current?.ProviderKey);
            await service.CloseSessionsAsync();
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EffectfulDraftCreation_IsSingleFlightButSettledIdenticalRequestCreatesAgain(bool projectScope)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-create-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Test root already exists.");
        Directory.CreateDirectory(root);
        var activity = new DraftActivity();
        try
        {
            var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home"); var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider, () => new DraftRuntime(activity)),
            });
            var project = projectScope ? await host.ProjectCatalog.UpsertFromPathAsync(projectPath) : null;
            var request = new WorkspaceCreateSessionRequest(Epoch, projectScope ? "project" : "global",
                project?.Id, project?.ProjectPath, "Effectful draft");
            var service = new WorkspaceService(host, Epoch);
            var startsBefore = Volatile.Read(ref activity.Starts);
            try
            {
                var original = service.CreateSessionAsync(request, CancellationToken.None);
                await activity.CreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.IsFalse(original.IsCompleted);
                Assert.AreEqual(startsBefore + 1, Volatile.Read(ref activity.Starts));
                Assert.AreEqual(1, Volatile.Read(ref activity.Creates));
                Assert.AreEqual(0, Volatile.Read(ref activity.Sends));
                Assert.AreEqual("busy", (await service.CreateSessionAsync(request, CancellationToken.None)).Status);
                Assert.AreEqual(startsBefore + 1, Volatile.Read(ref activity.Starts));
                Assert.AreEqual(1, Volatile.Read(ref activity.Creates));

                activity.ReleaseCreate.TrySetResult();
                var first = await original.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.AreEqual("ok", first.Status);
                Assert.IsNotNull(first.SessionId);
                var firstState = await host.RuntimeService.GetCurrentStateAsync(first.SessionId);
                Assert.IsNotNull(firstState.Entry);
                Assert.AreEqual(Provider.ProviderId.Value, firstState.Entry.ProviderId);

                // No receipt key exists: the identical settled request starts a new original.
                var second = await service.CreateSessionAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.AreEqual("ok", second.Status);
                Assert.IsNotNull(second.SessionId);
                Assert.AreNotEqual(first.SessionId, second.SessionId);
                Assert.AreEqual(startsBefore + 2, Volatile.Read(ref activity.Starts));
                Assert.AreEqual(2, Volatile.Read(ref activity.Creates));
                Assert.AreEqual(0, Volatile.Read(ref activity.Sends));
                Assert.AreEqual(0, Volatile.Read(ref activity.Resumes));
                Assert.AreEqual(firstState, await host.RuntimeService.GetCurrentStateAsync(first.SessionId));
                var secondState = await host.RuntimeService.GetCurrentStateAsync(second.SessionId);
                Assert.IsNotNull(secondState.Entry);
                Assert.AreNotEqual(firstState.Entry.AttachmentGeneration, secondState.Entry.AttachmentGeneration);
                var snapshot = await service.SnapshotAsync(new(), CancellationToken.None);
                Assert.HasCount(2, snapshot.Sessions);
                foreach (var id in new[] { first.SessionId, second.SessionId })
                    Assert.IsTrue(snapshot.Sessions.Any(session => session.Id == id
                        && session.WorkspacePath == (project?.ProjectPath ?? global)
                        && session.ProviderKey == Provider.ProviderId.Value));
            }
            finally
            {
                // Release the real provider work before draining RPC admission and disposing the host,
                // including when an assertion fails while creation is held.
                activity.ReleaseCreate.TrySetResult();
                await service.CloseSessionsAsync();
                await service.CloseImportsAsync();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task InvalidScopeStaleHostAndMissingProjectNeverAdmitCreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-create-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Test root already exists.");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            providers.RegisterOrReplace(Provider, () => new DraftRuntime());
            var count = 0;
            var rpc = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) =>
            { count++; throw new AssertFailedException("Invalid requests cannot create a session."); });
            var missing = Path.Combine(root, "missing");
            var samples = new[]
            {
                new WorkspaceCreateSessionRequest(Epoch, "global", "unexpected", null, null),
                new WorkspaceCreateSessionRequest(Epoch, "project", null, missing, null),
                new WorkspaceCreateSessionRequest(Epoch, "project", "unknown", null, null),
                new WorkspaceCreateSessionRequest(Epoch, "project", "unknown", "relative", null),
                new WorkspaceCreateSessionRequest(Epoch, "other", null, null, null),
                new WorkspaceCreateSessionRequest(Epoch, "global", null, null, " leading"),
                new WorkspaceCreateSessionRequest(Epoch, "global", null, null, new string('x', 257)),
            };
            foreach (var sample in samples)
            {
                var refused = await rpc.CreateSessionAsync(sample, CancellationToken.None);
                Assert.AreEqual("invalid_scope", refused.Status);
                Assert.IsNull(refused.ProjectPath);
            }
            Assert.AreEqual("stale_epoch", (await rpc.CreateSessionAsync(new(Guid.NewGuid().ToString("D"), "global", null, null, null), CancellationToken.None)).Status);
            Assert.AreEqual("project_missing", (await rpc.CreateSessionAsync(new(Epoch, "project", "unknown", missing, null), CancellationToken.None)).Status);
            var projectPath = Path.Combine(root, "archived");
            Directory.CreateDirectory(projectPath);
            var archived = await catalog.UpsertFromPathAsync(projectPath);
            archived.Archived = true;
            await catalog.SaveAsync(archived);
            Assert.AreEqual("project_missing", (await rpc.CreateSessionAsync(new(Epoch, "project", archived.Id, projectPath, null), CancellationToken.None)).Status);
            Assert.IsTrue((await catalog.GetByIdAsync(archived.Id))!.Archived);
            Assert.AreEqual("unconfigured", (await new WorkspaceService((string?)null)
                .CreateSessionAsync(new(Epoch, "global", null, null, null), CancellationToken.None)).Status);
            await using var unavailable = new ModelProviderRegistry();
            var noProvider = new WorkspaceService(reads, catalog, Epoch, unavailable, (_, _, _) =>
                throw new AssertFailedException("No provider means no admission."));
            Assert.AreEqual("provider_unavailable", (await noProvider.CreateSessionAsync(new(Epoch, "global", null, null, null), CancellationToken.None)).Status);
            await noProvider.CloseSessionsAsync();
            Assert.AreEqual(0, count);
            Assert.HasCount(1, await catalog.LoadAsync());
            await rpc.CloseSessionsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DuplicateAdmissionAndCancelledWaitRetainOriginalUntilShutdownAndUncertainty()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-create-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Test root already exists.");
        Directory.CreateDirectory(root);
        var pending = new TaskCompletionSource<SessionViewDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            providers.RegisterOrReplace(Provider, () => new DraftRuntime());
            var count = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rpc = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) => { count++; entered.TrySetResult(); return pending.Task; });
            var request = new WorkspaceCreateSessionRequest(Epoch, "global", null, null, null, Provider.ProviderId.Value);
            using var cancel = new CancellationTokenSource();
            var original = rpc.CreateSessionAsync(request, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await original);
            // The creation runs on the thread pool, admitted before the wait was canceled.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            providers.RegisterOrReplace(Provider with { IsEnabled = false }, () => throw new AssertFailedException("Replacement must not run."));
            Assert.AreEqual("busy", (await rpc.CreateSessionAsync(request, CancellationToken.None)).Status);
            var close = rpc.CloseSessionsAsync();
            Assert.IsFalse(close.IsCompleted);
            Assert.AreEqual("closed", (await rpc.CreateSessionAsync(request, CancellationToken.None)).Status);
            Assert.AreEqual(1, count);
            pending.SetException(new IOException("A journal write may have committed."));
            await close;
            providers.RegisterOrReplace(Provider, () => new DraftRuntime());
            var uncertain = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) =>
                Task.FromException<SessionViewDescriptor>(new IOException("A write may have committed.")));
            Assert.AreEqual("create_unconfirmed", (await uncertain.CreateSessionAsync(request, CancellationToken.None)).Status);
            await uncertain.CloseSessionsAsync();
        }
        finally { pending.TrySetCanceled(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CreationRunsOffTheThreadOfTheWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new WindowContext();
        var previous = SynchronizationContext.Current;
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            providers.RegisterOrReplace(Provider, () => throw new AssertFailedException("Descriptor admission must not initialize providers."));
            var caller = Environment.CurrentManagedThreadId;
            var ran = new TaskCompletionSource<(int Thread, SynchronizationContext? Context)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) =>
            {
                ran.TrySetResult((Environment.CurrentManagedThreadId, SynchronizationContext.Current));
                return Task.FromResult(new SessionViewDescriptor { SessionId = "created", Kind = SessionViewKind.GlobalSession, WorkingDirectory = root });
            });
            var builder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = string.Empty, Release = true });
            builder.AddWorkspaceService(service);
            await using var host = builder.Build();
            var answers = Channel.CreateUnbounded<string>();
            await using var session = host.OpenSession(new NeoRpcSessionIdentity("window", "page"), (json, _) =>
            {
                answers.Writer.TryWrite(json);
                return ValueTask.CompletedTask;
            });
            // The view gives the frames of the page to the host on the thread of the window: the handler runs elsewhere.
            SynchronizationContext.SetSynchronizationContext(window);
            _ = session.ReceiveAsync($$$"""{"neoastra":1,"kind":"invoke","id":"r1","command":"workspace.createSession","args":{"expectedHostEpoch":"{{{Epoch}}}","scope":"global"}}""");
            SynchronizationContext.SetSynchronizationContext(previous);
            var (thread, context) = await ran.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreNotEqual(caller, thread, "The session is not created on the thread of the window.");
            Assert.AreNotSame(window, context);
            using var answer = JsonDocument.Parse(await answers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.AreEqual("ok", answer.RootElement.GetProperty("value").GetProperty("status").GetString(), answer.RootElement.GetRawText());
            await service.CloseSessionsAsync(); await service.CloseImportsAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ALostAnswerIsAskedAgainByTheIdentifierOfItsRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pending = new TaskCompletionSource<SessionViewDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            await using var providers = new ModelProviderRegistry();
            providers.RegisterOrReplace(Provider, () => throw new AssertFailedException("Descriptor admission must not initialize providers."));
            var count = 0;
            var service = new WorkspaceService(reads, catalog, Epoch, providers, (_, _, _) => { Interlocked.Increment(ref count); return pending.Task; });
            var id = Guid.NewGuid().ToString("D");
            var request = new WorkspaceCreateSessionRequest(Epoch, "global", null, null, "Lost answer", RequestId: id);

            // The page stops waiting: the creation goes on, and its outcome is waited for again by its identifier.
            using (var cancel = new CancellationTokenSource())
            {
                var original = service.CreateSessionAsync(request, cancel.Token);
                cancel.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await original);
            }
            var outcome = service.CreateSessionOutcomeAsync(new(Epoch, id), CancellationToken.None);
            // The same request again is the same creation, never a second session.
            var again = service.CreateSessionAsync(request, CancellationToken.None);
            Assert.IsFalse(outcome.IsCompleted);
            pending.SetResult(new SessionViewDescriptor { SessionId = "created", Kind = SessionViewKind.GlobalSession, WorkingDirectory = root });
            var answer = await outcome.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual("ok", answer.Status);
            Assert.AreEqual("created", answer.SessionId);
            Assert.AreEqual("global", answer.Scope);
            Assert.AreEqual(answer, await again.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.AreEqual(1, count);

            // A request the host never admitted made no session; another host knows nothing of it.
            Assert.AreEqual("unknown", (await service.CreateSessionOutcomeAsync(new(Epoch, Guid.NewGuid().ToString("D")), CancellationToken.None)).Status);
            Assert.AreEqual("stale_epoch", (await service.CreateSessionOutcomeAsync(new(Guid.NewGuid().ToString("D"), id), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await service.CreateSessionOutcomeAsync(new(Epoch, "not-a-guid"), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await service.CreateSessionAsync(request with { RequestId = "not-a-guid" }, CancellationToken.None)).Status);
            await service.CloseSessionsAsync(); await service.CloseImportsAsync();
        }
        finally { pending.TrySetCanceled(); Directory.Delete(root, true); }
    }

    // The thread of a window: what is posted to it is never run, as a window busy with the creation would not.
    private sealed class WindowContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("The window is busy.");
    }

    private sealed class DraftActivity
    {
        public int Starts;
        public int Creates;
        public int Sends;
        public int Resumes;
        // The permission mode the last provider session was created in.
        public string? PermissionMode;
        public TaskCompletionSource CreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DraftRuntime(DraftActivity? activity = null, ModelProviderDescriptor? descriptor = null) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor ?? Provider;
        public Task StartAsync(CancellationToken token = default)
        {
            if (activity is not null) Interlocked.Increment(ref activity.Starts);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken token = default) => throw new NotSupportedException();
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();
        public async Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken token = default)
        {
            if (activity is not null)
            {
                Interlocked.Increment(ref activity.Creates);
                activity.PermissionMode = options.PermissionMode;
                activity.CreateEntered.TrySetResult();
                await activity.ReleaseCreate.Task.WaitAsync(token);
            }
            return new DraftSession(options.SessionId!, options.WorkingDirectory, activity, Descriptor);
        }
        public Task<IAgentSession> ResumeSessionAsync(string id, AgentSessionResumeOptions options, CancellationToken token = default)
        {
            if (activity is not null) Interlocked.Increment(ref activity.Resumes);
            return Task.FromResult<IAgentSession>(new DraftSession(id, options.WorkingDirectory, activity, Descriptor));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DraftSession(string id, string? path, DraftActivity? activity = null, ModelProviderDescriptor? descriptor = null) : IAgentSession
    {
        public ModelProviderId ProviderId => (descriptor ?? Provider).ProviderId;
        public string SessionId => id;
        public string? WorkspacePath => path;
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; yield break; }
        public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();
        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken token = default)
        {
            if (activity is not null) Interlocked.Increment(ref activity.Sends);
            throw new NotSupportedException();
        }
        public Task AbortAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken token = default) => throw new NotSupportedException();
        public Task CompactAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
