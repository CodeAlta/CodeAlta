using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.ModelServers;
using CodeAlta.ModelServers.HuggingFace;
using CodeAlta.ModelServers.Store;

namespace CodeAlta.Tests;

/// <summary>The <c>alta llama</c> commands, over a fake view of the model servers of two machines.</summary>
public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public void ModelServersSkill_NamesOnlyCommandsAndOptionsOfTheTree()
    {
        var skill = File.ReadAllText(BuiltinSkillPath("codealta-model-servers"));
        StringAssert.Contains(skill, "name: codealta-model-servers");
        StringAssert.Contains(skill, "`alta llama --help`");
        using var root = TempDirectory.Create();
        var (dispatcher, _) = LlamaDispatcher(root.Path);
        var commands = dispatcher.Describe(AltaCallerIdentity.Cli, root.Path).ToDictionary(static command => command.Path, StringComparer.Ordinal);
        var named = Regex.Matches(skill, "`alta ([^`]+)`").Select(static match => match.Groups[1].Value).ToArray();
        Assert.IsGreaterThan(20, named.Length, "the skill explains the important commands");
        foreach (var text in named)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var words = tokens.TakeWhile(static token => !token.StartsWith('-') && !token.StartsWith('<')).ToArray();
            var length = Enumerable.Range(1, words.Length).Reverse().FirstOrDefault(count => commands.ContainsKey(string.Join(' ', words.Take(count))));
            if (length == 0)
            {
                var group = string.Join(' ', words);
                Assert.IsTrue(commands.Keys.Any(path => path.StartsWith(group + " ", StringComparison.Ordinal)), $"`alta {text}` names no command of the tree");
                continue;
            }

            var command = commands[string.Join(' ', words.Take(length))];
            var options = command.Options.SelectMany(static option => option.Aliases.Prepend(option.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var option in tokens.Where(static token => token.StartsWith("--", StringComparison.Ordinal) && token != "--help"))
                Assert.IsTrue(options.Contains(option[2..]), $"`alta {text}`: `{command.Path}` has no option {option}");
        }
    }

    [TestMethod]
    public async Task Llama_StatusAndLists_ReadTheMachineNamed()
    {
        using var root = TempDirectory.Create();
        var (dispatcher, servers) = LlamaDispatcher(root.Path);

        var machines = await dispatcher.InvokeAsync(["llama", "machine", "list"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, machines.ExitCode, machines.Stderr);
        Assert.AreEqual(3, ReadJsonLines(machines.Stdout).Count(static record => record.GetProperty("type").GetString() == "alta.llama.machine"));

        var status = await dispatcher.InvokeAsync(["llama", "status", "--machine", "pc-ai"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, status.ExitCode, status.Stderr);
        var record = ReadJsonLines(status.Stdout).Single(static item => item.GetProperty("type").GetString() == "alta.llama.status");
        Assert.AreEqual("device-1", record.GetProperty("machine").GetString());
        Assert.AreEqual("rocm", record.GetProperty("recommended").GetString());
        Assert.IsTrue(record.GetProperty("updateAvailable").GetBoolean());

        var models = await dispatcher.InvokeAsync(["llama", "model", "list"], caller: AltaCallerIdentity.Cli);
        var model = ReadJsonLines(models.Stdout).Single(static item => item.GetProperty("type").GetString() == "alta.llama.model");
        Assert.AreEqual("unsloth/Qwen3-8B-GGUF:Q4_K_M", model.GetProperty("id").GetString());
        Assert.AreEqual("coder", model.GetProperty("usedBy")[0].GetString());

        var unknown = await dispatcher.InvokeAsync(["llama", "status", "--machine", "nowhere"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.NotFound, unknown.ExitCode);
        StringAssert.Contains(unknown.Stderr + unknown.Stdout, "llama.machineNotFound");
        Assert.IsEmpty(servers.Actions, "reading asks for no action");
    }

    [TestMethod]
    public async Task Llama_ServerAdd_BuildsTheDefinitionAndTellsTheSessionOfItsDownloads()
    {
        using var root = TempDirectory.Create();
        var (dispatcher, servers) = LlamaDispatcher(root.Path);
        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1" };

        var added = await dispatcher.InvokeAsync(["llama", "server", "add", "--model", "unsloth/Qwen3-8B-GGUF:q4_k_m", "--model", "unsloth/Gemma-4-GGUF:Q8_0", "--name", "Two models",
            "--context", "32768", "--gpu", "ROCm0", "--access", "this-machine"], caller: caller);
        Assert.AreEqual(AltaExitCodes.Success, added.ExitCode, added.Stdout + added.Stderr);
        var (machine, action, session) = servers.Actions.Single();
        Assert.AreEqual("local", machine);
        Assert.AreEqual("session-1", session, "the session that asks is told the end of what it starts");
        Assert.AreEqual("setServer", action.Action);
        var server = action.Server!;
        Assert.AreEqual("Two models", server.Name);
        Assert.AreEqual("several", server.Kind);
        Assert.AreEqual("cuda", server.Backend, "the best installed build of the machine");
        Assert.AreEqual("this-machine", server.Access);
        CollectionAssert.AreEqual(new[] { "ROCm0" }, server.Devices.ToArray());
        CollectionAssert.AreEqual(new[] { "unsloth/Qwen3-8B-GGUF:Q4_K_M", "unsloth/Gemma-4-GGUF:Q8_0" }, server.Models.Select(static model => model.Model).ToArray());
        Assert.IsTrue(server.Models.All(static model => model.Context == 32768));
        StringAssert.Contains(added.Stdout, "is downloaded first", "the model not on the disk is said to be downloaded");

        var invalid = await dispatcher.InvokeAsync(["llama", "server", "add", "--model", "not-a-model", "--name", "X"], caller: caller);
        Assert.AreEqual(AltaExitCodes.Usage, invalid.ExitCode);
        Assert.HasCount(1, servers.Actions, "an invalid server asks for nothing");
    }

    [TestMethod]
    public async Task Llama_ASessionWithoutTheShellOrWhoseCommandsAreReviewed_ReadsButStartsNothing()
    {
        using var root = TempDirectory.Create();
        var (dispatcher, servers) = LlamaDispatcher(root.Path, new AltaDisabledTools((sessionId, _) => Task.FromResult<IReadOnlySet<string>>(
            sessionId == "restricted" ? new HashSet<string> { "shell_command" } : new HashSet<string>())));
        var restricted = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "restricted" };
        foreach (var arguments in new[]
        {
            new[] { "llama", "build", "install" }, ["llama", "update", "now"], ["llama", "server", "start", "coder"], ["llama", "server", "test", "coder"],
            ["llama", "server", "add", "--model", "unsloth/Qwen3-8B-GGUF:Q4_K_M", "--name", "X"], ["llama", "update", "policy", "--schedule", "daily"],
        })
        {
            var denied = await dispatcher.InvokeAsync(arguments, caller: restricted);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, denied.ExitCode, string.Join(' ', arguments) + ": " + denied.Stdout + denied.Stderr);
            StringAssert.Contains(denied.Stdout + denied.Stderr, "llama.shellDisabled");
        }

        Assert.IsEmpty(servers.Actions, "nothing was asked of the machines");
        // What starts no program goes on: a stop, a download, the reading.
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["llama", "server", "stop", "coder"], caller: restricted)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["llama", "status"], caller: restricted)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["llama", "server", "add", "--model", "unsloth/Qwen3-8B-GGUF:Q4_K_M", "--name", "Later", "--stopped"],
            caller: restricted)).ExitCode, "a server saved stopped starts nothing");

        var (reviewed, reviewedServers) = LlamaDispatcher(root.Path, new AltaCommandReviewPolicy(AcceptsCommands: false));
        var refused = await reviewed.InvokeAsync(["llama", "build", "install"], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "reviewed" });
        Assert.AreEqual(AltaExitCodes.PolicyDenied, refused.ExitCode);
        StringAssert.Contains(refused.Stdout + refused.Stderr, "llama.startDenied");
        Assert.IsEmpty(reviewedServers.Actions);
    }

    [TestMethod]
    public async Task Llama_AMachineThisComputerDoesNotManage_IsReadButNotChanged()
    {
        using var root = TempDirectory.Create();
        var (dispatcher, servers) = LlamaDispatcher(root.Path);
        Assert.AreEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["llama", "server", "list", "--machine", "mac"], caller: AltaCallerIdentity.Cli)).ExitCode);
        var refused = await dispatcher.InvokeAsync(["llama", "model", "download", "unsloth/Qwen3-8B-GGUF:Q4_K_M", "--machine", "mac"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, refused.ExitCode);
        StringAssert.Contains(refused.Stdout + refused.Stderr, "llama.notManaged");
        Assert.IsEmpty(servers.Actions);

        var download = await dispatcher.InvokeAsync(["llama", "model", "download", "hf.co/unsloth/Qwen3-8B-GGUF:q4_k_m", "--machine", "pc-ai"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, download.ExitCode, download.Stderr);
        Assert.AreEqual("unsloth/Qwen3-8B-GGUF:Q4_K_M", servers.Actions.Single().Action.Model);
        Assert.AreEqual("device-1", servers.Actions.Single().Machine);
    }

    [TestMethod]
    public async Task Llama_UpdateAll_RunsOnEveryManagedMachine_WithOneBuildForAll()
    {
        using var root = TempDirectory.Create();
        var (dispatcher, servers) = LlamaDispatcher(root.Path);
        var now = await dispatcher.InvokeAsync(["llama", "update", "now", "--all", "--build", "b12000"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, now.ExitCode, now.Stderr);
        CollectionAssert.AreEqual(new[] { "local", "device-1" }, servers.Actions.Select(static entry => entry.Machine).ToArray(), "the machines it manages, not mac");
        Assert.IsTrue(servers.Actions.All(static entry => entry.Action is { Action: "updateNow", Build: "b12000" }));
        Assert.HasCount(2, ReadJsonLines(now.Stdout).Where(static item => item.GetProperty("type").GetString() == "alta.llama.task"));

        servers.Actions.Clear();
        var at = await dispatcher.InvokeAsync(["llama", "update", "at", DateTimeOffset.Now.AddHours(2).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), "--all"],
            caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, at.ExitCode, at.Stderr);
        Assert.IsTrue(servers.Actions.Count == 2 && servers.Actions.All(static entry => entry.Action is { Action: "planUpdate", At: not null, Build: null }));

        var both = await dispatcher.InvokeAsync(["llama", "update", "now", "--all", "--machine", "pc-ai"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Usage, both.ExitCode);
    }

    [TestMethod]
    public async Task Llama_HubShow_SaysTheFitOfEachQuantizationOnTheManagedMachines()
    {
        using var root = TempDirectory.Create();
        var (dispatcher, servers) = LlamaDispatcher(root.Path);
        var shown = await dispatcher.InvokeAsync(["llama", "hub", "show", "https://huggingface.co/unsloth/Qwen3-8B-GGUF"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, shown.ExitCode, shown.Stderr);
        CollectionAssert.AreEqual(new[] { "local", "device-1" }, servers.HubMachines.ToArray(), "the machines this computer manages");
        var quant = ReadJsonLines(shown.Stdout).Single(static item => item.GetProperty("type").GetString() == "alta.llama.hub.quantization");
        Assert.AreEqual("unsloth/Qwen3-8B-GGUF:Q4_K_M", quant.GetProperty("model").GetString());
        Assert.AreEqual("gpu", quant.GetProperty("fits")[0].GetProperty("fit").GetString());
    }

    [TestMethod]
    public void Llama_SandboxedSessions_ReadTheModelServersOnly()
    {
        Assert.IsTrue(AltaSandboxGate.Allows("alta llama status"));
        Assert.IsTrue(AltaSandboxGate.Allows("alta llama server log"));
        Assert.IsFalse(AltaSandboxGate.Allows("alta llama server add"));
        Assert.IsFalse(AltaSandboxGate.Allows("alta llama model download"));
        Assert.IsTrue(AltaCommandRegistryOptions.Node.AllowsBuiltInRoot("llama"), "the sessions of a node manage the model servers of their machine");
    }

    private static (AltaCommandDispatcher Dispatcher, FakeModelServers Servers) LlamaDispatcher(string root, object? extra = null)
    {
        var servers = new FakeModelServers();
        var registry = new AltaCommandRegistry();
        var services = new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = root }).Add(registry).Add<IAltaModelServers>(servers);
        if (extra is AltaDisabledTools tools) services.Add(tools);
        if (extra is AltaCommandReviewPolicy policy) services.Add(policy);
        return (new AltaCommandDispatcher(registry, services), servers);
    }

    private static string BuiltinSkillPath(string name)
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "BuiltinSkills", name, "SKILL.md");
        if (File.Exists(copied)) return copied;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var source = Path.Combine(folder.FullName, "CodeAlta.Catalog", "BuiltinSkills", name, "SKILL.md");
            if (File.Exists(source)) return source;
        }

        throw new FileNotFoundException($"The skill {name} was not found.");
    }

    // This computer runs CUDA and a server `coder` of one model; pc-ai, managed, has ROCm with an update; mac is used, not managed.
    private sealed class FakeModelServers : IAltaModelServers
    {
        public List<(string Machine, ModelHostAction Action, string? Session)> Actions { get; } = [];

        public List<string> HubMachines { get; } = [];

        public Task<IReadOnlyList<AltaModelServersMachine>> ListMachinesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AltaModelServersMachine>>(
            [
                new("local", "this-pc", true, "online", true, true, null),
                new("device-1", "pc-ai", false, "online", true, true, null),
                new("device-2", "mac", false, "online", false, true, null),
            ]);

        public Task<ModelHostStatus> StatusAsync(string machine, CancellationToken cancellationToken)
        {
            var backend = machine == "device-1" ? "rocm" : "cuda";
            var build = new InstalledBuild("b12000", 12000, backend, null, "builds", 1, DateTimeOffset.UnixEpoch, [new LlamaDevice(backend == "rocm" ? "ROCm0" : "CUDA0", "GPU", 16384, 16000, "gpu-0")],
                null, true, false, false, "ggml-org");
            var hardware = new HardwareInventory("windows", "x64", null, 64L << 30, [new GpuInfo("gpu-0", "amd", "Radeon", 16L << 30, false, "gfx1200", null)],
                new MachineLibraries(null, null, null, false, true, null), []);
            var model = new StoredModel("unsloth/Qwen3-8B-GGUF:Q4_K_M", "unsloth/Qwen3-8B-GGUF", "Q4_K_M", "abc", [], 5_000_000_000, 5_000_000_000, false, "ready", null, null, null);
            var server = new ServerView(new ServerDefinition { Id = "coder", Name = "Coder", Backend = backend, Port = 9931, Models = [new ServerModel { Model = model.Id }] }, ServerStatus.Stopped);
            return Task.FromResult(new ModelHostStatus(hardware, [build],
                [new BuildCandidate("vulkan", null, "fits", "ok", 10, []), new BuildCandidate(backend, null, "fits", "best", 50, [])],
                new LlamaRelease("b12110", 12110, DateTimeOffset.UnixEpoch, "0.9.0", []), new ModelHostSettings(), null, [], "models", 5_000_000_000, 100_000_000_000,
                [model], [], [server], [], [], [], 1));
        }

        public Task<ModelHostActionResult> RunAsync(string machine, ModelHostAction action, string? sessionId, CancellationToken cancellationToken)
        {
            Actions.Add((machine, action, sessionId));
            return Task.FromResult(action.Action switch
            {
                "setServer" => new ModelHostActionResult { Server = new ServerView(action.Server! with { Id = action.Server!.Id.Length == 0 ? "new" : action.Server!.Id }, ServerStatus.Stopped) },
                "controlServer" => new ModelHostActionResult { Server = new ServerView(new ServerDefinition { Id = "coder", Name = "Coder", Backend = "cuda" }, ServerStatus.Stopped) },
                "download" => new ModelHostActionResult { Download = new DownloadInfo("d1", action.Model!, "queued", 0, 1, 0, null, null, 1, DateTimeOffset.UnixEpoch, null) },
                _ => new ModelHostActionResult(),
            });
        }

        public Task<IReadOnlyList<HubModel>> SearchHubAsync(string? search, string? author, string? pipeline, string sort, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<HubModel>>([new HubModel("unsloth/Qwen3-8B-GGUF", "unsloth", 10, 2, null, "text-generation", [], false, 1)]);

        public Task<AltaHubModel> ReadHubModelAsync(string repo, IReadOnlyList<string> machines, CancellationToken cancellationToken)
        {
            HubMachines.AddRange(machines);
            var details = new HubModelDetails(new HubModel(repo, "unsloth", 10, 2, null, "text-generation", [], false, 1), "abc", "apache-2.0", [], 8_000_000_000, "qwen3", 40960, []);
            return Task.FromResult(new AltaHubModel(details, [new AltaHubQuantization("Q4_K_M", 4, 5_000_000_000, 0, 1, [.. machines.Select(static machine => new AltaHubFit(machine, new FitResult("gpu", "fits", true), false))])]));
        }

        public Task<(string Provider, IReadOnlyDictionary<string, string?> Results)> UseInSessionsAsync(string machine, string server, string name, IReadOnlyList<string> machines,
            CancellationToken cancellationToken)
            => Task.FromResult<(string, IReadOnlyDictionary<string, string?>)>(("llama-" + server, machines.ToDictionary(static item => item, static string? (_) => null)));
    }
}
