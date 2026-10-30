using System.Collections;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Agent.Telemetry;
using CodeAlta.CodexAppServer;
using CodeAlta.Plugin.Acp;
using CodeAlta.Plugin.CodexCli;

namespace CodeAlta.Tests;

/// <summary>
/// When CodeAlta exports its traces, the agents it starts do not inherit the variables of OpenTelemetry (they would report
/// the same runs twice), while its shell commands keep them.
/// </summary>
[TestClass]
[DoNotParallelize] // The variables of the process are changed.
public sealed class OpenTelemetryEnvironmentTests
{
    private const string Endpoint = OpenTelemetryEnvironment.EndpointVariable;
    private const string ServiceName = OpenTelemetryEnvironment.ServiceNameVariable;

    [TestMethod]
    public void ExportsTraces_WithAnEndpoint_UnlessTheSdkIsDisabled()
    {
        static bool Exports(params (string Name, string Value)[] variables)
            => OpenTelemetryEnvironment.ExportsTraces(name => variables.FirstOrDefault(variable => variable.Name == name).Value);

        Assert.IsFalse(Exports());
        Assert.IsFalse(Exports((ServiceName, "ci")), "a service name alone sends nothing");
        Assert.IsFalse(Exports((Endpoint, "  ")));
        Assert.IsTrue(Exports((Endpoint, "http://collector:4318")));
        Assert.IsTrue(Exports((OpenTelemetryEnvironment.TracesEndpointVariable, "http://collector:4318/v1/traces")));
        Assert.IsFalse(Exports((Endpoint, "http://collector:4318"), (OpenTelemetryEnvironment.SdkDisabledVariable, "TRUE")));
        Assert.IsTrue(Exports((Endpoint, "http://collector:4318"), (OpenTelemetryEnvironment.SdkDisabledVariable, "false")));
    }

    [TestMethod]
    public void TheAgentsDoNotInheritTheOpenTelemetryVariables_OnlyWhenCodeAltaExportsItsTraces()
    {
        var exporting = new Hashtable { [Endpoint] = "http://collector:4318", ["otel_service_name"] = "ci", ["OTEL_EXPORTER_OTLP_HEADERS"] = "x=1", ["PATH"] = "/bin" };
        CollectionAssert.AreEqual(new[] { "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_HEADERS", "otel_service_name" },
            OpenTelemetryEnvironment.VariablesHiddenFromAgents(exporting).ToArray());

        Assert.IsEmpty(OpenTelemetryEnvironment.VariablesHiddenFromAgents(new Hashtable { [ServiceName] = "ci", ["PATH"] = "/bin" }),
            "without an endpoint CodeAlta sends nothing, and an agent keeps what it was given");
        exporting[OpenTelemetryEnvironment.SdkDisabledVariable] = "true";
        Assert.IsEmpty(OpenTelemetryEnvironment.VariablesHiddenFromAgents(exporting));
    }

    [TestMethod]
    public void TheClaudeCodeCli_DoesNotInheritThem_AlsoInTheSandbox_ButKeepsThoseOfItsProvider()
    {
        var account = Path.Combine(Path.GetTempPath(), "claude-account");
        var options = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude-code",
            ConfigDirectory = account,
            EnvironmentVariables = new Dictionary<string, string> { ["OTEL_RESOURCE_ATTRIBUTES"] = "team=agents" },
        };
        using (new VariablesScope((Endpoint, null), (ServiceName, null)))
        {
            var plain = ClaudeCodeLauncher.Create("/tools/claude/claude", options, new ClaudeCodeLaunchKey("/work", null, null), null, null, withTools: true);
            Assert.IsFalse(plain.Environment.ContainsKey(Endpoint), "CodeAlta exports nothing: the CLI is started as before");
        }

        using var scope = new VariablesScope((Endpoint, "http://collector:4318"), (ServiceName, "ci"));
        var launch = ClaudeCodeLauncher.Create("/tools/claude/claude", options, new ClaudeCodeLaunchKey("/work", null, null), null, null, withTools: true);
        var hosted = ClaudeCodeLauncher.ForHost(launch, "/tools/claude/claude", options, withoutApiKey: false);

        foreach (var environment in new[] { launch.Environment, hosted.Environment })
        {
            Assert.IsTrue(environment.TryGetValue(Endpoint, out var endpoint) && endpoint is null, "the inherited endpoint is removed");
            Assert.IsTrue(environment.TryGetValue(ServiceName, out var service) && service is null);
            Assert.AreEqual("team=agents", environment["OTEL_RESOURCE_ATTRIBUTES"], "what the user gave the provider stays");
        }

        Assert.IsFalse(hosted.PassedVariables.Contains(Endpoint), "nor is it passed into the sandbox");
    }

    [TestMethod]
    public async Task AnAcpAgent_DoesNotInheritThem_ButKeepsThoseOfItsProvider()
    {
        using var scope = new VariablesScope((Endpoint, "http://collector:4318"), (ServiceName, "ci"));
        var host = new RecordingHost();
        var request = new AcpConnectionRequest("my-agent", [], new Dictionary<string, string> { [ServiceName] = "my-agent" }, Path.GetTempPath(),
            TimeSpan.FromSeconds(30)) { Host = host };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => AcpProcessConnectionFactory.Instance.StartAsync(request, static _ => { }, CancellationToken.None));

        var environment = host.Launch!.Environment;
        Assert.IsTrue(environment.TryGetValue(Endpoint, out var endpoint) && endpoint is null);
        Assert.AreEqual("my-agent", environment[ServiceName], "what the user gave the provider stays");
    }

    [TestMethod]
    public async Task TheCodexAppServer_DoesNotInheritThem()
    {
        using var scope = new VariablesScope((Endpoint, "http://collector:4318"), (ServiceName, "ci"));
        var host = new RecordingHost();
        var request = new CodexCliConnectionRequest(new CodexAppServerCommand("/tools/codex/codex", ["app-server"], null, IsPinned: false),
            Path.Combine(Path.GetTempPath(), "codex-home"), new Dictionary<string, string>(), Path.GetTempPath(), TimeSpan.FromSeconds(30)) { Host = host };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new CodexCliProcessConnectionFactory(null).StartAsync(request, static _ => { }, CancellationToken.None));

        var environment = host.Launch!.Environment;
        Assert.IsTrue(environment.TryGetValue(Endpoint, out var endpoint) && endpoint is null);
        Assert.IsTrue(environment.TryGetValue(ServiceName, out var service) && service is null);
    }

    [TestMethod]
    public void ShellCommands_KeepThem()
    {
        using var temp = TestTempDirectory.Create();
        using var scope = new VariablesScope((Endpoint, "http://collector:4318"), (ServiceName, "ci"));

        var startInfo = AgentBuiltInToolFactory.CreateShellStartInfo("echo", temp.Path);

        Assert.AreEqual("http://collector:4318", startInfo.Environment[Endpoint], "a command (dotnet test in a pipeline) reports its own work");
        Assert.AreEqual("ci", startInfo.Environment[ServiceName]);
    }

    // Sets variables of the process, and gives them back their values at the end.
    private sealed class VariablesScope : IDisposable
    {
        private readonly (string Name, string? Value)[] _inherited;

        public VariablesScope(params (string Name, string? Value)[] variables)
        {
            _inherited = [.. variables.Select(static variable => (variable.Name, Environment.GetEnvironmentVariable(variable.Name)))];
            foreach (var (name, value) in variables) Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _inherited) Environment.SetEnvironmentVariable(name, value);
        }
    }

    // A sandbox that records what it is asked to start, and starts nothing.
    private sealed class RecordingHost : IAgentProcessHost
    {
        public string Key => "recording";

        public AgentProcessLaunch? Launch { get; private set; }

        public Task<IAgentHostedProcess> StartAsync(AgentProcessLaunch launch, CancellationToken cancellationToken)
        {
            Launch = launch;
            throw new InvalidOperationException("Nothing is started.");
        }
    }
}
