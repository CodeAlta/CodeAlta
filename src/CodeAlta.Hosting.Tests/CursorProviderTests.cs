using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Cursor;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class CursorProviderTests
{
    private const string LoginUrl = "https://cursor.example.test/login";
    private const string AssistantText = "Scripted cursor assistant text.";

    [TestMethod]
    public void Registry_AcceptsCursorAsItsOwnRuntime()
    {
        using var root = new TempRoot();
        var definition = Definition(root.ConfigPath);
        definition.ApiKey = null;

        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, root.Path, null, out var descriptor, out var createRuntime));
        Assert.AreEqual(CursorModelProviderRuntime.ProviderType, descriptor.ProviderType);
        Assert.AreNotEqual("xai", descriptor.ProviderType);
        Assert.AreNotEqual("claude-code", descriptor.ProviderType);
        Assert.AreNotEqual("openai-chat", descriptor.ProviderType);
        Assert.IsNotNull(createRuntime);

        var runtime = createRuntime();
        Assert.IsInstanceOfType<CursorModelProviderRuntime>(runtime);
        Assert.AreEqual(AgentTransportKind.CursorAgentCli, ((CursorModelProviderRuntime)runtime).RuntimeDescriptor.TransportKind);
    }

    [TestMethod]
    public async Task BrowserLogin_SurfacesTheHttpsUrl()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        var prompts = new List<ProviderLoginPrompt>();

        var status = await ConfiguredProviderLogin.LoginAsync(Definition(root.ConfigPath), root.Path, "browser",
            (prompt, _) => { prompts.Add(prompt); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.AreEqual(LoginUrl, prompts.Single().Uri.AbsoluteUri.TrimEnd('/'));
        Assert.IsNull(prompts[0].UserCode);
        Assert.IsTrue(status.SignedIn);
        Assert.IsTrue(status.Usable);
        StringAssert.Contains(root.Log(), "browser-login");
        Assert.IsFalse(root.Log().Contains("device-no-browser", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DeviceLogin_SurfacesTheUrlAndUserCodeWithoutOpeningABrowser()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: true, userCode: "ABCD-1234");
        var prompts = new List<ProviderLoginPrompt>();

        var status = await ConfiguredProviderLogin.LoginAsync(Definition(root.ConfigPath), root.Path, "device",
            (prompt, _) => { prompts.Add(prompt); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.AreEqual(LoginUrl, prompts.Single().Uri.AbsoluteUri.TrimEnd('/'));
        Assert.AreEqual("ABCD-1234", prompts[0].UserCode);
        Assert.IsTrue(status.SignedIn);
        StringAssert.Contains(root.Log(), "device-no-browser");
        Assert.IsFalse(root.Log().Contains("browser-login", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AlreadyAuthenticatedCli_IsSignedInWithoutAnApiKeyOrLogin()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        var definition = Definition(root.ConfigPath);
        Assert.IsNull(definition.ApiKey);

        var status = await ConfiguredProviderLogin.GetStatusAsync(definition, root.Path, CancellationToken.None);

        Assert.IsTrue(status.SignedIn);
        Assert.IsTrue(status.Usable);
        Assert.AreEqual("status", root.Log().Trim());
    }

    [TestMethod]
    public async Task SignOut_ClearsTheSignedInViewUntilLogin()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        var definition = Definition(root.ConfigPath);

        Assert.IsTrue(await ConfiguredProviderLogin.SignOutAsync(definition, root.Path, CancellationToken.None));
        var signedOut = await ConfiguredProviderLogin.GetStatusAsync(definition, root.Path, CancellationToken.None);
        Assert.IsFalse(signedOut.SignedIn);
        Assert.IsFalse(root.Log().Contains("logout", StringComparison.Ordinal));
        Assert.AreEqual(1, root.Log().Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.Trim() == "status"));

        await ConfiguredProviderLogin.LoginAsync(definition, root.Path, "browser", static (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        var signedIn = await ConfiguredProviderLogin.GetStatusAsync(definition, root.Path, CancellationToken.None);
        Assert.IsTrue(signedIn.SignedIn);
    }

    [TestMethod]
    public async Task AcpTurn_ReturnsTheStreamedAssistantTextAndAnswersPermission()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null, assistant: AssistantText);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        var runtime = (CursorModelProviderRuntime)createRuntime();
        string? streamed = null;

        var response = await runtime.CreateTurnExecutor().ExecuteTurnAsync(new AgentTurnRequest
        {
            Provider = runtime.RuntimeDescriptor,
            ProviderId = descriptor.ProviderId,
            SessionId = "session-cursor",
            RunId = new AgentRunId("run-cursor"),
            ModelId = "composer-2.5",
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Say one sentence.")])],
            Tools = [],
            State = new AgentSessionState
            {
                SessionId = "session-cursor",
                ProtocolFamily = CursorModelProviderRuntime.ProtocolFamily,
                ProviderKey = descriptor.ProviderId.Value,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        }, (delta, _) =>
        {
            streamed = delta.Text;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        var text = string.Concat(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(part => part.Value));
        Assert.AreEqual(AssistantText, text);
        Assert.AreEqual(AssistantText, streamed);
        var log = root.Log();
        StringAssert.Contains(log, "initialize");
        StringAssert.Contains(log, "authenticate cursor_login");
        StringAssert.Contains(log, "session/new");
        StringAssert.Contains(log, "model composer-2.5");
        StringAssert.Contains(log, "session/prompt");
        StringAssert.Contains(log, "permission");
        StringAssert.Contains(log, "allow-once");
        StringAssert.Contains(log, "selected");
    }

    [TestMethod]
    public async Task Probe_ReportsTheModelsTheCliPrints()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out _, out var createRuntime));
        var runtime = (CursorModelProviderRuntime)createRuntime();

        var probe = await runtime.ProbeAsync(CancellationToken.None);

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability);
        Assert.HasCount(2, probe.Models);
        Assert.AreEqual("auto", probe.Models![0].Id);
        Assert.AreEqual("Auto", probe.Models[0].DisplayName);
        Assert.AreEqual("composer-2.5", probe.Models[1].Id);
        Assert.AreEqual("Composer 2.5", probe.Models[1].DisplayName);
        StringAssert.Contains(root.Log(), "models");
    }

    [TestMethod]
    public void AgentCommand_PrefersTheCursorShimOverAnEarlierAgentExe()
    {
        using var root = new TempRoot();
        var other = Path.Combine(root.Path, "other");
        var cursor = Path.Combine(root.Path, "cursor");
        var older = Path.Combine(cursor, "versions", "2026.10.14-0f9edde");
        var newer = Path.Combine(cursor, "versions", "2026.10.18-ea085cb");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        File.WriteAllText(Path.Combine(other, "agent.exe"), "");
        File.WriteAllText(Path.Combine(cursor, "agent.cmd"), "@echo off\r\nrem cursor-agent\r\n");
        File.WriteAllText(Path.Combine(older, "node.exe"), "");
        File.WriteAllText(Path.Combine(older, "index.js"), "");
        var node = Path.Combine(newer, "node.exe");
        var index = Path.Combine(newer, "index.js");
        File.WriteAllText(node, "");
        File.WriteAllText(index, "");

        var start = CursorProcess.Resolve(
            new CursorCliLaunch("agent", ["--extra"], null),
            "acp",
            other + Path.PathSeparator + cursor,
            windowsShims: true);

        Assert.AreEqual(node, start.FileName);
        Assert.AreEqual(index, start.Arguments[0]);
        Assert.AreEqual("--extra", start.Arguments[1]);
        Assert.AreEqual("acp", start.Arguments[2]);
        Assert.AreEqual("agent.cmd", start.InvokedAs);
    }

    private static CodeAltaProviderDocument Definition(string configPath)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "CodeAlta.Tests.CursorCli.dll");
        Assert.IsTrue(File.Exists(dll), dll);
        return new CodeAltaProviderDocument
        {
            ProviderKey = "cursor",
            ProviderType = CursorModelProviderRuntime.ProviderType,
            Enabled = true,
            Command = "dotnet",
            Arguments = ["exec", dll, "--config", configPath],
        };
    }

    private static void WriteConfig(TempRoot root, bool authenticated, bool device, string? userCode, string? assistant = null)
    {
        File.WriteAllText(root.ConfigPath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["authenticated"] = authenticated,
            ["device"] = device,
            ["url"] = LoginUrl,
            ["userCode"] = userCode,
            ["assistant"] = assistant ?? AssistantText,
            ["models"] = new[] { "auto - Auto", "Available models", "composer-2.5 - Composer 2.5", "Tip: use --model <id> to switch." },
            ["log"] = root.LogPath,
        }));
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = Directory.CreateTempSubdirectory("codealta-cursor-").FullName;
            ConfigPath = System.IO.Path.Combine(Path, "cli.json");
            LogPath = System.IO.Path.Combine(Path, "cli.log");
        }

        public string Path { get; }
        public string ConfigPath { get; }
        public string LogPath { get; }

        public string Log() => File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
