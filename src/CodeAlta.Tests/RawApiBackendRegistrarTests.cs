using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using CodeAlta.App;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class RawApiBackendRegistrarTests
{
    [TestMethod]
    public async Task RegisterConfiguredBackends_RegistersConfiguredProviders()
    {
        using var temp = TempDirectory.Create();
        var openAiKeyName = $"CODEALTA_OPENAI_{Guid.NewGuid():N}";
        var anthropicKeyName = $"CODEALTA_ANTHROPIC_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(openAiKeyName, "openai-test-key");
        Environment.SetEnvironmentVariable(anthropicKeyName, "anthropic-test-key");

        try
        {
            File.WriteAllText(
                Path.Combine(temp.Path, "config.toml"),
                $$"""
                [providers.openai_chat]
                display_name = "OpenAI Chat"
                type = "openai-chat"
                api_key_env = "{{openAiKeyName}}"

                [providers.openai_responses]
                display_name = "OpenAI Responses"
                type = "openai-responses"
                api_key_env = "{{openAiKeyName}}"

                [providers.anthropic]
                display_name = "Anthropic"
                type = "anthropic"
                api_key_env = "{{anthropicKeyName}}"

                [providers.vertex]
                display_name = "Vertex"
                type = "vertex-ai"
                project = "sample-project"
                location = "europe-west4"
                """);

            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
            var factory = new AgentBackendFactory();

            var descriptors = RawApiBackendRegistrar.RegisterConfiguredBackends(
                factory,
                store,
                Path.Combine(temp.Path, "machine", "agents"));

            var descriptorsById = descriptors.ToDictionary(
                static descriptor => descriptor.BackendId.Value,
                static descriptor => descriptor.DisplayName,
                StringComparer.OrdinalIgnoreCase);

            CollectionAssert.AreEquivalent(
                new[] { "openai_chat", "openai_responses", "anthropic", "vertex" },
                descriptors.Select(static descriptor => descriptor.BackendId.Value).ToArray());

            Assert.AreEqual("OpenAI Responses", descriptorsById["openai_responses"]);
            Assert.AreEqual("OpenAI Chat", descriptorsById["openai_chat"]);
            Assert.AreEqual("Anthropic", descriptorsById["anthropic"]);
            Assert.AreEqual("Vertex", descriptorsById["vertex"]);

            Assert.IsTrue(factory.IsRegistered("openai_responses"));
            Assert.IsTrue(factory.IsRegistered("openai_chat"));
            Assert.IsTrue(factory.IsRegistered("anthropic"));
            Assert.IsTrue(factory.IsRegistered("vertex"));

            await using var responsesBackend = factory.Create("openai_responses");
            await using var chatBackend = factory.Create("openai_chat");
            await using var anthropicBackend = factory.Create("anthropic");
            await using var googleBackend = factory.Create("vertex");

            Assert.IsInstanceOfType<OpenAIResponsesAgentBackend>(responsesBackend);
            Assert.IsInstanceOfType<OpenAIChatAgentBackend>(chatBackend);
            Assert.IsInstanceOfType<AnthropicAgentBackend>(anthropicBackend);
            Assert.IsInstanceOfType<GoogleGenAIAgentBackend>(googleBackend);
        }
        finally
        {
            Environment.SetEnvironmentVariable(openAiKeyName, null);
            Environment.SetEnvironmentVariable(anthropicKeyName, null);
        }
    }

    [TestMethod]
    public void RegisterConfiguredBackends_SkipsProvidersWithoutUsableCredentials()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openrouter]
            type = "openai-responses"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var factory = new AgentBackendFactory();

        var descriptors = RawApiBackendRegistrar.RegisterConfiguredBackends(
            factory,
            store,
            Path.Combine(temp.Path, "machine", "agents"));

        Assert.AreEqual(0, descriptors.Count);
        Assert.AreEqual(0, factory.ListRegisteredBackends().Count);
    }

    [TestMethod]
    public void RegisterConfiguredBackends_UsesSingleProviderDisplayNameForDescriptor()
    {
        using var temp = TempDirectory.Create();
        var minimaxKeyName = $"MINIMAX_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(minimaxKeyName, "minimax-test-key");

        try
        {
            File.WriteAllText(
                Path.Combine(temp.Path, "config.toml"),
                $$"""
                [providers.minimax]
                display_name = "MiniMax 2.7"
                type = "openai-chat"
                api_key_env = "{{minimaxKeyName}}"
                api_url = "https://api.minimax.io/v1"
                """);

            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
            var factory = new AgentBackendFactory();

            var descriptors = RawApiBackendRegistrar.RegisterConfiguredBackends(
                factory,
                store,
                Path.Combine(temp.Path, "machine", "agents"));

            Assert.AreEqual(1, descriptors.Count);
            Assert.AreEqual("minimax", descriptors[0].BackendId.Value);
            Assert.AreEqual("MiniMax 2.7", descriptors[0].DisplayName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(minimaxKeyName, null);
        }
    }

    [TestMethod]
    public async Task RegisterConfiguredBackends_RegistersCodexSubscriptionProvider()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex]
            type = "codex"
            model = "gpt-5.3-codex"
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var factory = new AgentBackendFactory();

        var descriptors = RawApiBackendRegistrar.RegisterConfiguredBackends(
            factory,
            store,
            Path.Combine(temp.Path, "machine", "agents"));

        Assert.AreEqual(1, descriptors.Count);
        Assert.AreEqual("codex", descriptors[0].BackendId.Value);
        Assert.AreEqual("Codex", descriptors[0].DisplayName);
        Assert.IsTrue(factory.IsRegistered("codex"));

        await using var backend = factory.Create("codex");
        Assert.IsInstanceOfType<OpenAIResponsesAgentBackend>(backend);

        var models = await backend.ListModelsAsync().ConfigureAwait(false);
        Assert.AreEqual(
            "gpt-5.2|gpt-5.3-codex|gpt-5.4|gpt-5.4-mini|gpt-5.5",
            string.Join('|', models.Select(static model => model.Id)));
        Assert.IsTrue(models.All(static model => model.Provider == "codex"));
    }

    [TestMethod]
    public void RegisterConfiguredBackends_CodexSubscriptionRejectsMissingExperimentalOptIn()
    {
        using var temp = TempDirectory.Create();
        var definition = new CodeAltaProviderDocument
        {
            ProviderKey = "codex",
            Enabled = true,
            ProviderType = "codex",
            DisplayName = "Codex Sub",
            Model = "gpt-5.3-codex",
            ApiUrl = "https://chatgpt.com/backend-api/codex",
            AuthSource = "codealta_oauth",
            MaxConcurrentRequests = 16,
            TextVerbosity = "medium",
            IncludeEncryptedReasoning = true,
            ModelDiscovery = "static",
            SendResponsesBetaHeader = true,
            SendInstallationId = false,
            InstallationIdSource = "codealta_state",
            Experimental = false,
        };

        var factory = new AgentBackendFactory();
        Assert.ThrowsExactly<InvalidOperationException>(
            () => RawApiBackendRegistrar.RegisterOrReplaceConfiguredBackends(
                factory,
                [definition],
                Path.Combine(temp.Path, "machine", "agents")));
    }

    [TestMethod]
    public async Task RegisterConfiguredBackends_StartAsync_DoesNotPersistProviderDescriptors()
    {
        using var temp = TempDirectory.Create();
        var minimaxKeyName = $"MINIMAX_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(minimaxKeyName, "minimax-test-key");

        try
        {
            File.WriteAllText(
                Path.Combine(temp.Path, "config.toml"),
                $$"""
                [providers.compat]
                display_name = "MiniMax 2.7"
                type = "openai-chat"
                api_key_env = "{{minimaxKeyName}}"
                api_url = "https://api.minimax.io/v1"
                """);

            var stateRoot = Path.Combine(temp.Path, "machine", "agents");
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
            var factory = new AgentBackendFactory();

            _ = RawApiBackendRegistrar.RegisterConfiguredBackends(
                factory,
                store,
                stateRoot);

            await using var chatBackend = factory.Create("compat");
            await chatBackend.StartAsync().ConfigureAwait(false);

            Assert.IsFalse(Directory.Exists(Path.Combine(stateRoot, "providers")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(minimaxKeyName, null);
        }
    }

    [TestMethod]
    public async Task RegisterConfiguredBackends_CreateSession_PersistsOnlySessionJournal()
    {
        using var temp = TempDirectory.Create();
        var minimaxKeyName = $"MINIMAX_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(minimaxKeyName, "minimax-test-key");

        try
        {
            File.WriteAllText(
                Path.Combine(temp.Path, "config.toml"),
                $$"""
                [providers.compat]
                display_name = "MiniMax 2.7"
                type = "openai-chat"
                api_key_env = "{{minimaxKeyName}}"
                api_url = "https://api.minimax.io/v1"
                """);

            var stateRoot = Path.Combine(temp.Path, "machine", "agents");
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
            var factory = new AgentBackendFactory();

            _ = RawApiBackendRegistrar.RegisterConfiguredBackends(
                factory,
                store,
                stateRoot);

            await using var chatBackend = factory.Create("compat");
            await using var session = await chatBackend.CreateSessionAsync(
                    new AgentSessionCreateOptions
                    {
                        Model = "MiniMax-M2.7",
                        WorkingDirectory = temp.Path,
                        OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                    })
                .ConfigureAwait(false);

            var sessionsRoot = Path.Combine(stateRoot, "sessions");
            Assert.IsTrue(Directory.Exists(sessionsRoot));
            Assert.AreEqual(1, Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories).Count());
            Assert.IsFalse(Directory.Exists(Path.Combine(stateRoot, "providers")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(minimaxKeyName, null);
        }
    }

    [TestMethod]
    public void RawApiProviderDefaultsCatalog_MiniMaxDefaults_PrependReasoningFieldAndMergeExtraBody()
    {
        var profile = RawApiProviderDefaultsCatalog.ApplyProfileDefaults(
            LocalAgentTransportKind.OpenAIChatCompletions,
            "minimax",
            new Uri("https://api.minimax.io/v1"),
            new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = true,
                ReasoningFieldNames = ["reasoning_content", "reasoning"],
            });

        Assert.IsFalse(profile.SupportsDeveloperRole);
        CollectionAssert.AreEqual(
            new[] { "reasoning_details[0].text", "reasoning_content", "reasoning" },
            profile.ReasoningFieldNames.ToArray());

        var extraBody = RawApiProviderDefaultsCatalog.ApplyOpenAIExtraBodyDefaults(
            LocalAgentTransportKind.OpenAIChatCompletions,
            "minimax",
            new Uri("https://api.minimax.io/v1"),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["reasoning_split"] = false,
                ["custom_flag"] = true,
            });

        Assert.IsNotNull(extraBody);
        Assert.AreEqual(false, extraBody!["reasoning_split"]);
        Assert.AreEqual(true, extraBody["custom_flag"]);
    }

    [TestMethod]
    public async Task RegisterConfiguredBackends_OpenAICompatibleProviderWithoutModelsEndpoint_FallsBackToModelsDevCatalog()
    {
        using var temp = TempDirectory.Create();
        using var server = new StaticStatusServer(HttpStatusCode.NotFound, "404 Page not found");
        await using var modelCatalog = CreateModelCatalog();
        var minimaxKeyName = $"MINIMAX_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(minimaxKeyName, "minimax-test-key");

        try
        {
            File.WriteAllText(
                Path.Combine(temp.Path, "config.toml"),
                $$"""
                [providers.minimax]
                display_name = "MiniMax 2.7"
                type = "openai-chat"
                api_key_env = "{{minimaxKeyName}}"
                api_url = "{{server.BaseUri}}"
                """);

            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
            var factory = new AgentBackendFactory();

            var descriptors = RawApiBackendRegistrar.RegisterConfiguredBackends(
                factory,
                store,
                Path.Combine(temp.Path, "machine", "agents"),
                modelCatalog);

            Assert.AreEqual(1, descriptors.Count);
            Assert.AreEqual("MiniMax 2.7", descriptors[0].DisplayName);

            await using var chatBackend = factory.Create("minimax");
            var models = await chatBackend.ListModelsAsync().ConfigureAwait(false);

            CollectionAssert.AreEquivalent(
                new[] { "MiniMax-M2.7", "MiniMax-M2.7-highspeed" },
                models.Select(static model => model.Id).ToArray());
            Assert.AreEqual("MiniMax-M2.7", models[0].DisplayName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(minimaxKeyName, null);
        }
    }

    [TestMethod]
    public async Task RegisterConfiguredBackends_SingleModelId_ExposesOnlyConfiguredModel()
    {
        using var temp = TempDirectory.Create();
        await using var modelCatalog = CreateModelCatalog();
        var minimaxKeyName = $"MINIMAX_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(minimaxKeyName, "minimax-test-key");

        try
        {
            File.WriteAllText(
                Path.Combine(temp.Path, "config.toml"),
                $$"""
                [providers.minimax]
                display_name = "MiniMax 2.7"
                type = "openai-chat"
                api_key_env = "{{minimaxKeyName}}"
                api_url = "http://127.0.0.1:9/v1"
                single_model_id = " MiniMax-M2.7 "
                """);

            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
            var factory = new AgentBackendFactory();

            _ = RawApiBackendRegistrar.RegisterConfiguredBackends(
                factory,
                store,
                Path.Combine(temp.Path, "machine", "agents"),
                modelCatalog);

            await using var chatBackend = factory.Create("minimax");
            var models = await chatBackend.ListModelsAsync().ConfigureAwait(false);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("MiniMax-M2.7", models[0].Id);
            Assert.AreEqual("MiniMax-M2.7", models[0].DisplayName);
            Assert.AreEqual(1000000L, models[0].Capabilities?["contextWindow"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(minimaxKeyName, null);
        }
    }

    [TestMethod]
    public async Task RegisterConfiguredBackends_AnthropicSingleModelId_ExposesOnlyConfiguredModel()
    {
        using var temp = TempDirectory.Create();
        await using var modelCatalog = CreateModelCatalog();
        var minimaxKeyName = $"MINIMAX_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(minimaxKeyName, "minimax-test-key");

        try
        {
            File.WriteAllText(
                Path.Combine(temp.Path, "config.toml"),
                $$"""
                [providers.minimax]
                display_name = "MiniMax 2.7"
                type = "anthropic"
                api_key_env = "{{minimaxKeyName}}"
                api_url = "https://api.minimax.io/anthropic"
                single_model_id = " MiniMax-M2.7 "
                """);

            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
            var factory = new AgentBackendFactory();

            _ = RawApiBackendRegistrar.RegisterConfiguredBackends(
                factory,
                store,
                Path.Combine(temp.Path, "machine", "agents"),
                modelCatalog);

            await using var anthropicBackend = factory.Create("minimax");
            var models = await anthropicBackend.ListModelsAsync().ConfigureAwait(false);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("MiniMax-M2.7", models[0].Id);
            Assert.AreEqual("MiniMax-M2.7", models[0].DisplayName);
            Assert.AreEqual(1000000L, models[0].Capabilities?["contextWindow"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(minimaxKeyName, null);
        }
    }

    private static ModelsDevCatalogService CreateModelCatalog()
        => new(
            ModelsDevDatabaseJson.Deserialize(
                """
                {
                  "minimax": {
                    "id": "minimax",
                    "name": "MiniMax (minimax.io)",
                    "models": {
                      "MiniMax-M2.7": {
                        "id": "MiniMax-M2.7",
                        "name": "MiniMax-M2.7",
                        "tool_call": true,
                        "limit": { "context": 1000000, "output": 128000 }
                      },
                      "MiniMax-M2.7-highspeed": {
                        "id": "MiniMax-M2.7-highspeed",
                        "name": "MiniMax-M2.7-highspeed",
                        "tool_call": true,
                        "limit": { "context": 1000000, "output": 128000 }
                      }
                    }
                  }
                }
                """),
            new ModelsDevCatalogServiceOptions());

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "raw-api-backend-registrar-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class StaticStatusServer : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly TcpListener _listener = CreateListener();
        private readonly Task _acceptLoopTask;
        private readonly string _body;
        private readonly HttpStatusCode _statusCode;

        public Uri BaseUri { get; }

        public StaticStatusServer(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
            _listener.Start();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/");
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cancellationTokenSource.Token));
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _listener.Stop();
            try
            {
                _acceptLoopTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _cancellationTokenSource.Dispose();
        }

        private static TcpListener CreateListener() => new(IPAddress.Loopback, 0);

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null || line.Length == 0)
                    {
                        break;
                    }
                }

                var contentBytes = Encoding.UTF8.GetBytes(_body);
                var responseBytes = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(int)_statusCode} {_statusCode}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {contentBytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(contentBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
