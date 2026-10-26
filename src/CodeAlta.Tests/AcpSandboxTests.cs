using System.IO.Pipelines;
using CodeAlta.Acp;
using CodeAlta.Agent.Runtime;
using CodeAlta.Plugin.Acp;

namespace CodeAlta.Tests;

/// <summary>An ACP agent in the sandbox of a session: started by the host of the sandbox, with what it needs.</summary>
[TestClass]
public sealed class AcpSandboxTests
{
    [TestMethod]
    public async Task AnAgentInTheSandbox_IsStartedByItsHost_WithItsHomeHostsAndVariables_AndEndsWithItsClient()
    {
        var agent = new AcpFakeAgent();
        var host = new FakeHost(agent);
        var settings = new AcpAgentSettings("npx", ["--yes", "@github/copilot@1.0.103", "--acp"],
            new Dictionary<string, string> { ["COPILOT_GITHUB_TOKEN"] = "token-of-the-test", ["COPILOT_AUTO_UPDATE"] = "true" }, "github-copilot-cli", null);
        var executable = Path.Combine(Path.GetTempPath(), "nodejs", "npx.cmd");
        var request = new AcpConnectionRequest(executable, settings.Arguments, settings.Environment, Path.GetTempPath(), TimeSpan.FromSeconds(30))
        {
            Host = host,
            Needs = AcpAgentQuirks.For(settings).SandboxNeeds(settings, executable, "copilot") with { LocalPorts = [50123] },
        };

        var client = await AcpProcessConnectionFactory.Instance.StartAsync(request, static _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.IsNotNull(client.InitializeResponse, "the protocol runs over the streams of the process the host started");
        var launch = host.Launch!;
        Assert.AreEqual(executable, launch.FileName);
        Assert.AreEqual("acp-copilot", launch.OwnHome, "a home of its own, where it signs in once");
        Assert.AreEqual("token-of-the-test", launch.Environment["COPILOT_GITHUB_TOKEN"]);
        CollectionAssert.AreEquivalent(new[] { "COPILOT_GITHUB_TOKEN", "COPILOT_AUTO_UPDATE" }, launch.PassedVariables.ToArray(), "the variables of its provider pass into its sandbox");
        Assert.AreEqual("true", launch.Environment["COPILOT_AUTO_UPDATE"], "its provider says better than CodeAlta");
        Assert.IsNull(launch.Environment["CLAUDECODE"], "nor does it run as an agent inside an agent");
        CollectionAssert.Contains(launch.Hosts.ToArray(), "registry.npmjs.org", "npx downloads the agent");
        CollectionAssert.Contains(launch.Hosts.ToArray(), "api.github.com");
        CollectionAssert.AreEqual(new[] { 50123 }, launch.LocalPorts.ToArray());
        Assert.IsEmpty(launch.ReadWriteFolders, "nothing of the home folder of the user");
        Assert.IsTrue(launch.ReadOnlyFolders.Any(static folder => folder.EndsWith("nodejs", StringComparison.Ordinal)), "the folder of its executable");

        await client.DisposeAsync();
        Assert.IsTrue(host.Ended, "the process of the sandbox ends with its client");
    }

    [TestMethod]
    public void AnAgentCodeAltaDoesNotKnow_NeedsTheFolderOfItsExecutable_AndTheHostsOfItsProvider()
    {
        var settings = new AcpAgentSettings("C:/agents/my-agent.exe", ["acp"], new Dictionary<string, string>(), null, null)
        {
            SandboxHosts = [" api.example.com ", "*.example.com", ""],
        };

        var needs = AcpAgentQuirks.For(settings).SandboxNeeds(settings, Path.GetFullPath("C:/agents/my-agent.exe"), "my-agent");

        Assert.HasCount(1, needs.ReadOnlyFolders);
        CollectionAssert.AreEqual(new[] { "api.example.com", "*.example.com" }, needs.Hosts.ToArray(), "its hosts are those its provider lists, with those of the profile");
        Assert.IsEmpty(needs.PassedVariables);
        Assert.IsEmpty(needs.Environment);
        Assert.AreEqual("acp-my-agent", needs.OwnHome);
    }

    [TestMethod]
    public async Task TheClientOfAnAgentInTheSandbox_EndsItsProcessFirst_SinceAReadOfItsOutputMayNotStopOnCancellation()
    {
        var host = new FakeHost(new AcpFakeAgent()) { OutputIgnoresCancellation = true };
        var settings = new AcpAgentSettings("my-agent", [], new Dictionary<string, string>(), null, null);
        var request = new AcpConnectionRequest("my-agent", [], settings.Environment, Path.GetTempPath(), TimeSpan.FromSeconds(30)) { Host = host };
        var client = await AcpProcessConnectionFactory.Instance.StartAsync(request, static _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(host.Ended);
    }

    // A host that runs the fake agent on pipes, as the sandbox runs a process, and records what it was asked to start.
    private sealed class FakeHost(AcpFakeAgent agent) : IAgentProcessHost
    {
        public string Key => "test";

        // As the output of a container: a read ends with data or with the end of the process, not with its token.
        public bool OutputIgnoresCancellation { get; init; }

        public AgentProcessLaunch? Launch { get; private set; }

        public bool Ended { get; private set; }

        public Task<IAgentHostedProcess> StartAsync(AgentProcessLaunch launch, CancellationToken cancellationToken)
        {
            Launch = launch;
            var toAgent = new Pipe();
            var fromAgent = new Pipe();
            var connection = new AcpFakeConnection(agent, toAgent.Reader.AsStream(), fromAgent.Writer.AsStream(),
                new AcpConnectionRequest(launch.FileName, launch.Arguments, new Dictionary<string, string>(), launch.WorkingDirectory, TimeSpan.FromSeconds(30)));
            agent.Connections.Enqueue(connection);
            connection.Start();
            var output = fromAgent.Reader.AsStream();
            return Task.FromResult<IAgentHostedProcess>(new Process(this, toAgent.Writer.AsStream(), OutputIgnoresCancellation ? new DeafStream(output) : output, connection));
        }

        private sealed class DeafStream(Stream inner) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, CancellationToken.None);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => inner.ReadAsync(buffer, offset, count, CancellationToken.None);

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class Process(FakeHost host, Stream input, Stream output, AcpFakeConnection connection) : IAgentHostedProcess
        {
            private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int Id => 1;

            public Stream StandardInput => input;

            public Stream StandardOutput => output;

            public Stream StandardError { get; } = new MemoryStream();

            public Task<int> Exited => _exited.Task;

            public void Kill() => connection.Exit();

            public ValueTask DisposeAsync()
            {
                host.Ended = true;
                connection.Exit();
                _exited.TrySetResult(0);
                return ValueTask.CompletedTask;
            }
        }
    }
}
