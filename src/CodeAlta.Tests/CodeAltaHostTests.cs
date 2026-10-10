using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaHostTests
{
    [TestMethod]
    public async Task CreateAsync_HeadlessWithoutPlugins_ConstructsAndDisposesRuntimeServices()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var options = new CodeAltaHostOptions
        {
            GlobalRoot = Path.Combine(temp.Path, "home"),
            CurrentProjectPath = projectRoot,
            IsHeadless = true,
            HasInteractiveUi = false,
            PluginSafeMode = true,
            StartPlugins = false,
            RawArguments = ["--headless"],
        };

        await using var host = await CodeAltaHost.CreateAsync(options, CancellationToken.None);

        Assert.AreEqual(Path.GetFullPath(options.GlobalRoot), host.CatalogOptions.GlobalRoot);
        Assert.AreEqual(projectRoot, host.CurrentProject.ProjectPath);
        Assert.IsFalse((await host.ProjectCatalog.LoadAsync(CancellationToken.None).ConfigureAwait(false)).Any());
        Assert.IsNotNull(host.ProjectCatalog);
        Assert.IsNotNull(host.SessionViewCatalog);
        Assert.IsNotNull(host.SkillCatalog);
        Assert.IsNotNull(host.AgentHub);
        Assert.IsNotNull(host.RuntimeService);
        Assert.IsNotNull(host.ProjectFileSearchService);
        Assert.IsNotNull(host.PluginRuntime);
    }

    [TestMethod]
    public async Task CreateAsync_OpensTheApplicationDatabaseOfItsStateRootAndSharesItWithItsPlugins()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var home = Path.Combine(temp.Path, "home");
        var developerState = Path.Combine(home, "dev");
        var options = new CodeAltaHostOptions
        {
            GlobalRoot = home,
            StateRoot = developerState,
            CurrentProjectPath = projectRoot,
            IsHeadless = true,
            HasInteractiveUi = false,
            RawArguments = ["--headless"],
            PluginBuiltIns = [new CodeAlta.Plugins.BuiltInPluginDefinition { Id = "sample", DisplayName = "Sample", PluginType = typeof(SamplePlugin), Factory = static () => new SamplePlugin() }],
        };
        ApplicationDatabase database;
        await using (var host = await CodeAltaHost.CreateAsync(options, CancellationToken.None))
        {
            database = host.ApplicationDatabase;
            var plugin = host.PluginRuntime.ActivePlugins.Single();
            var pluginDatabase = plugin.RuntimeContext.Services.Database;

            Assert.AreEqual(Path.Combine(developerState, "data", "alta.sqlite3"), database.DatabasePath);
            Assert.AreSame(database, host.PluginRuntime.ApplicationDatabase);
            Assert.IsTrue(pluginDatabase.HasDatabase);
            Assert.AreEqual("sample_", pluginDatabase.TablePrefix);
            await pluginDatabase.MigrateAsync(1, async (connection, from, to, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE sample_rows (id INTEGER);";
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            // The list of sessions and the plugin write to one file.
            await host.SessionViewCatalog.JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
            Assert.AreEqual(1, await database.GetVersionAsync("plugin:sample").ConfigureAwait(false));
            Assert.AreEqual(1, await database.GetVersionAsync("session_cache").ConfigureAwait(false));
        }

        Assert.IsFalse(File.Exists(Path.Combine(home, "data", "alta.sqlite3")), "The developer instance never opens the database of the other instance.");
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await database.WriteAsync("test", (connection, token) => ValueTask.CompletedTask).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CreateAsync_UsesTheDatabaseOfThePrestartedRuntimeAndDoesNotDisposeIt()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var home = Path.Combine(temp.Path, "home");
        Directory.CreateDirectory(home);
        await using var runtime = new CodeAlta.Plugins.PluginRuntimeManager();
        await runtime.StartAsync(new CodeAlta.Plugins.PluginRuntimeManagerOptions { GlobalRoot = home, StateRoot = home, IsHeadless = true }).ConfigureAwait(false);
        var options = new CodeAltaHostOptions
        {
            GlobalRoot = home,
            CurrentProjectPath = projectRoot,
            IsHeadless = true,
            HasInteractiveUi = false,
            PrestartedPluginRuntime = runtime,
        };

        await using (var host = await CodeAltaHost.CreateAsync(options, CancellationToken.None).ConfigureAwait(false))
        {
            Assert.AreSame(runtime.ApplicationDatabase, host.ApplicationDatabase);
        }

        await runtime.ApplicationDatabase!.WriteAsync("test", (connection, token) => ValueTask.CompletedTask).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CreateAsync_DoesNotDisposeADatabaseItWasGiven()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var home = Path.Combine(temp.Path, "home");
        Directory.CreateDirectory(home);
        await using var database = ApplicationDatabase.Create(new CatalogOptions { GlobalRoot = home });
        var options = new CodeAltaHostOptions
        {
            GlobalRoot = home,
            CurrentProjectPath = projectRoot,
            IsHeadless = true,
            HasInteractiveUi = false,
            PluginSafeMode = true,
            StartPlugins = false,
            ApplicationDatabase = database,
        };

        await using (var host = await CodeAltaHost.CreateAsync(options, CancellationToken.None).ConfigureAwait(false))
        {
            Assert.AreSame(database, host.ApplicationDatabase);
        }

        await database.WriteAsync("test", (connection, token) => ValueTask.CompletedTask).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CreateAsync_ConfiguresHostRegisteredModelProviderRuntimes()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var ProviderId = new ModelProviderId("test-provider");
        var options = new CodeAltaHostOptions
        {
            GlobalRoot = Path.Combine(temp.Path, "home"),
            CurrentProjectPath = projectRoot,
            IsHeadless = true,
            HasInteractiveUi = false,
            PluginSafeMode = true,
            StartPlugins = false,
            ConfigureModelProviders = registry => registry.RegisterOrReplaceSessionRuntime(new ModelProviderDescriptor(new ModelProviderId(ProviderId.Value), "Test Provider"), () => new TestModelProviderRuntime(ProviderId)),
        };

        await using var host = await CodeAltaHost.CreateAsync(options, CancellationToken.None);

        var handle = await host.AgentHub.StartSessionAsync(
                new AgentSessionCreateOptions
                {
                    ProviderKey = ProviderId.Value,
                    WorkingDirectory = projectRoot,
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                },
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual("test-session", handle.SessionId);
    }

    [TestMethod]
    public async Task CreateProjectSessionAsync_PersistsTransientCurrentProject()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var providerId = new ModelProviderId("test-provider");
        var options = new CodeAltaHostOptions
        {
            GlobalRoot = Path.Combine(temp.Path, "home"),
            CurrentProjectPath = projectRoot,
            IsHeadless = true,
            HasInteractiveUi = false,
            PluginSafeMode = true,
            StartPlugins = false,
            ConfigureModelProviders = registry => registry.RegisterOrReplaceSessionRuntime(new ModelProviderDescriptor(new ModelProviderId(providerId.Value), "Test Provider"), () => new TestModelProviderRuntime(providerId)),
        };

        await using var host = await CodeAltaHost.CreateAsync(options, CancellationToken.None);
        Assert.IsFalse((await host.ProjectCatalog.LoadAsync(CancellationToken.None).ConfigureAwait(false)).Any());

        var session = await host.RuntimeService.CreateProjectSessionAsync(
                host.CurrentProject,
                new SessionExecutionOptions
                {
                    ProviderId = providerId,
                    ProviderKey = providerId.Value,
                    WorkingDirectory = projectRoot,
                    ProjectRoots = [projectRoot],
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                },
                title: "Test session",
                CancellationToken.None)
            .ConfigureAwait(false);

        var projects = await host.ProjectCatalog.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, projects.Count);
        Assert.AreEqual(host.CurrentProject.Id, projects[0].Id);
        Assert.AreEqual(projects[0].Id, session.ProjectRef);
    }

    [TestMethod]
    public async Task CreateProjectSessionAsync_DoesNotKeepTransientProjectWhenSessionStartFails()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var providerId = new ModelProviderId("test-provider");
        var options = new CodeAltaHostOptions
        {
            GlobalRoot = Path.Combine(temp.Path, "home"),
            CurrentProjectPath = projectRoot,
            IsHeadless = true,
            HasInteractiveUi = false,
            PluginSafeMode = true,
            StartPlugins = false,
            ConfigureModelProviders = registry => registry.RegisterOrReplaceSessionRuntime(new ModelProviderDescriptor(new ModelProviderId(providerId.Value), "Test Provider"), () => new FailingModelProviderRuntime(providerId)),
        };

        await using var host = await CodeAltaHost.CreateAsync(options, CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.RuntimeService.CreateProjectSessionAsync(
            host.CurrentProject,
            new SessionExecutionOptions
            {
                ProviderId = providerId,
                ProviderKey = providerId.Value,
                WorkingDirectory = projectRoot,
                ProjectRoots = [projectRoot],
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            },
            title: "Test session",
            CancellationToken.None));

        Assert.IsFalse((await host.ProjectCatalog.LoadAsync(CancellationToken.None).ConfigureAwait(false)).Any());
    }

    [TestMethod]
    public async Task CreateAsync_ArchivedCurrentProjectIsVisibleInMemoryWithoutSaving()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var catalogOptions = new CatalogOptions { GlobalRoot = Path.Combine(temp.Path, "home") };
        var archivedProject = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "project",
            Name = "project",
            DisplayName = "project",
            ProjectPath = projectRoot,
            Archived = true,
        };
        await new ProjectCatalog(catalogOptions).SaveAsync(archivedProject, CancellationToken.None).ConfigureAwait(false);
        var options = new CodeAltaHostOptions
        {
            GlobalRoot = catalogOptions.GlobalRoot,
            CurrentProjectPath = projectRoot,
            IsHeadless = true,
            HasInteractiveUi = false,
            PluginSafeMode = true,
            StartPlugins = false,
        };

        await using var host = await CodeAltaHost.CreateAsync(options, CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(host.CurrentProject.Archived);
        var persisted = await host.ProjectCatalog.GetByIdAsync(archivedProject.Id, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(persisted);
        Assert.IsTrue(persisted.Archived);
    }

    private sealed class TestModelProviderRuntime(ModelProviderId ProviderId) : ITestModelProviderSessionRuntime
    {
        public ModelProviderId ProviderId { get; } = ProviderId;

        public string DisplayName => "Test Provider";

        public Task StartAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>(Array.Empty<AgentModelInfo>());

        public async IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new TestAgentSession(ProviderId, options.SessionId ?? "test-session"));

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        private sealed class TestAgentSession(ModelProviderId ProviderId, string sessionId) : IAgentSession
        {
            public ModelProviderId ProviderId { get; } = ProviderId;

            public string SessionId { get; } = sessionId;

            public string? WorkspacePath => null;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.CompletedTask.ConfigureAwait(false);
                yield break;
            }

            public IDisposable Subscribe(Action<AgentEvent> handler) => NullSubscription.Instance;

            public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
                => Task.FromResult(new AgentRunId("test-run"));

            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
                => Task.FromResult(options.ExpectedRunId ?? new AgentRunId("test-run"));

            public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task CompactAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        }

        private sealed class NullSubscription : IDisposable
        {
            public static readonly NullSubscription Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class FailingModelProviderRuntime(ModelProviderId ProviderId) : ITestModelProviderSessionRuntime
    {
        public ModelProviderId ProviderId { get; } = ProviderId;

        public string DisplayName => "Test Provider";

        public Task StartAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>(Array.Empty<AgentModelInfo>());

        public async IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Session start failed.");

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class SamplePlugin : CodeAlta.Plugins.Abstractions.PluginBase
    {
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAltaHostTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public static TempDirectory Create() => new();

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
