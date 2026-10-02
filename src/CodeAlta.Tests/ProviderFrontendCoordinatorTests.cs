using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Events;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;
using CodeAlta.ViewModels;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProviderFrontendCoordinatorTests
{
    [TestMethod]
    [DataRow("codex", "codealta_oauth")]
    [DataRow("copilot", "github_device_flow")]
    [DataRow("xai", "xai_browser_oauth")]
    public void LoadProviderDefinitions_OmittedSettingsRemainDefaultInEditor(string providerType, string authSource)
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var content = $$"""
            [providers.{{providerType}}]
            type = "{{providerType}}"
            """;
        File.WriteAllText(options.ConfigPath, content);
        var coordinator = CreateCoordinator(options);
        var service = new ModelProviderDialogService(coordinator);

        var definition = service.LoadDefinitions().Single();
        var item = ModelProviderEditorItemViewModel.FromDocument(definition);

        Assert.IsTrue(item.UseDefaultDisplayName);
        Assert.IsTrue(item.UseDefaultModel);
        Assert.IsTrue(item.UseDefaultReasoningEffort);
        Assert.IsTrue(item.UseDefaultApiUrl);
        Assert.IsTrue(item.UseDefaultModelsDevProviderId);
        Assert.IsTrue(item.UseDefaultSingleModelId);
        Assert.IsTrue(item.UseDefaultModelsIncludeRegex);
        Assert.IsTrue(item.UseDefaultAuthSource);
        Assert.IsTrue(item.UseDefaultAccountId);
        Assert.IsTrue(item.UseDefaultModelDiscovery);
        Assert.IsTrue(item.UseDefaultResponseTransport);
        Assert.AreEqual(authSource, item.AuthSource);
        Assert.AreEqual($"{providerType}_endpoint_with_static_fallback", item.ModelDiscovery);
        Assert.IsTrue(coordinator.HasAnyEnabledProviders());
        Assert.AreEqual(content, File.ReadAllText(options.ConfigPath), "Loading the editor must not rewrite configuration.");

        var edited = item.ToDocument();
        Assert.IsNull(edited.DisplayName);
        Assert.IsNull(edited.ApiUrl);
        Assert.IsNull(edited.AuthSource);
        Assert.IsNull(edited.ModelDiscovery);
        Assert.IsNull(edited.ResponseTransport);

        var store = new CodeAltaConfigStore(options);
        var runtimeDefinition = store.LoadGlobalProviderDefinitions().Single();
        Assert.IsNotNull(runtimeDefinition.DisplayName);
        Assert.IsNotNull(runtimeDefinition.AuthSource);
        Assert.AreEqual($"{providerType}_endpoint_with_static_fallback", runtimeDefinition.ModelDiscovery);
        if (providerType == "codex")
        {
            Assert.AreEqual("https://api.openai.com/v1", runtimeDefinition.ApiUrl);
            Assert.AreEqual("http", runtimeDefinition.ResponseTransport);
        }

        store.SaveGlobalProviderDefinitions([edited]);
        var saved = store.LoadGlobalConfigContent();
        Assert.IsFalse(saved.Contains("display_name", StringComparison.Ordinal));
        Assert.IsFalse(saved.Contains("api_url", StringComparison.Ordinal));
        Assert.IsFalse(saved.Contains("auth_source", StringComparison.Ordinal));
        Assert.IsFalse(saved.Contains("model_discovery", StringComparison.Ordinal));
        Assert.IsFalse(saved.Contains("response_transport", StringComparison.Ordinal));
        Assert.IsTrue(ModelProviderEditorItemViewModel.FromDocument(service.LoadDefinitions().Single()).UseDefaultModelDiscovery);
    }

    [TestMethod]
    public void LoadProviderDefinitions_PreservesDisabledProviderAndCustomEditorSettings()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        File.WriteAllText(options.ConfigPath, """
            [providers.codex]
            type = "codex"
            enabled = false
            display_name = " My ChatGPT "
            model = " gpt-5.4 "
            reasoning_effort = " low "
            api_url = " http://localhost:5111/v1 "
            model_discovery = " static "
            response_transport = " websocket_with_http_fallback "
            max_concurrent_requests = 2
            """);
        var coordinator = CreateCoordinator(options);

        var item = ModelProviderEditorItemViewModel.FromDocument(coordinator.LoadProviderDefinitions().Single());

        Assert.IsFalse(item.Enabled);
        Assert.IsFalse(coordinator.HasAnyEnabledProviders());
        Assert.IsFalse(item.UseDefaultDisplayName);
        Assert.IsFalse(item.UseDefaultModel);
        Assert.IsFalse(item.UseDefaultReasoningEffort);
        Assert.IsFalse(item.UseDefaultApiUrl);
        Assert.IsFalse(item.UseDefaultModelDiscovery);
        Assert.IsFalse(item.UseDefaultResponseTransport);
        Assert.IsTrue(item.UseDefaultAuthSource);
        var definition = item.ToDocument();
        Assert.AreEqual("My ChatGPT", definition.DisplayName);
        Assert.AreEqual("gpt-5.4", definition.Model);
        Assert.AreEqual("low", definition.ReasoningEffort);
        Assert.AreEqual("http://localhost:5111/v1", definition.ApiUrl);
        Assert.AreEqual("static", definition.ModelDiscovery);
        Assert.AreEqual("websocket_with_http_fallback", definition.ResponseTransport);
        Assert.AreEqual(2, definition.MaxConcurrentRequests);
    }

    [TestMethod]
    public void TryBuildActiveProviderTestResult_UsesReadyProviderState()
    {
        var definition = new CodeAltaProviderDocument
        {
            ProviderKey = "copilot",
            ProviderType = "copilot",
        };
        var providerState = new ModelProviderState(ModelProviderIds.Copilot, "Copilot")
        {
            Availability = ModelProviderAvailability.Ready,
            StatusMessage = "Ready",
        };
        providerState.Models.Add(new AgentModelInfo("gpt-4.1"));
        providerState.Models.Add(new AgentModelInfo("gpt-4o"));

        var reused = ProviderFrontendCoordinator.TryBuildActiveProviderTestResult(
            definition,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
            {
                [definition.ProviderKey] = providerState,
            },
            out var result);

        Assert.IsTrue(reused);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, result.ModelCount);
        StringAssert.Contains(result.Message, "Using active model provider");
    }

    [TestMethod]
    public void TryBuildActiveProviderTestResult_UsesFailureStatusForProvider()
    {
        var definition = new CodeAltaProviderDocument
        {
            ProviderKey = "codex",
            ProviderType = "codex",
        };
        var providerState = new ModelProviderState(ModelProviderIds.Codex, "Codex")
        {
            Availability = ModelProviderAvailability.Failed,
            StatusMessage = "Codex startup failed.",
        };

        var reused = ProviderFrontendCoordinator.TryBuildActiveProviderTestResult(
            definition,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
            {
                [definition.ProviderKey] = providerState,
            },
            out var result);

        Assert.IsTrue(reused);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, result.ModelCount);
        Assert.AreEqual("Codex startup failed.", result.Message);
    }

    [TestMethod]
    public void TryBuildActiveProviderTestResult_DoesNotReuseMissingProviderState()
    {
        var definition = new CodeAltaProviderDocument
        {
            ProviderKey = "openai",
            ProviderType = "openai-chat",
        };
        var reused = ProviderFrontendCoordinator.TryBuildActiveProviderTestResult(
            definition,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase),
            out var result);

        Assert.IsFalse(reused);
        Assert.AreEqual(default, result);
    }

    private static ProviderFrontendCoordinator CreateCoordinator(CatalogOptions options)
    {
        var states = new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase);
        var events = new FrontendEventPublisher(new InlineUiDispatcher());
        var initialization = new ModelProviderInitializationCoordinator(
            new ModelProviderInitializationService(new ModelProviderRegistry()),
            [], states, static action => action(), events);
        return new ProviderFrontendCoordinator(null, options, initialization, states,
            static action => action(), events, static (_, _, _) => { });
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodeAlta.ProviderEditor.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
