using System.Collections.Frozen;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Fleet.MachineSetup;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Create my server (<see cref="FleetServerService.CreateServer"/>) makes the owner's recovery key with the server it claims,
/// and gives its words once with the <c>finished</c> step, for the page to show them.
/// </summary>
[TestClass]
public sealed class FleetServerCreateRecoveryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CreateMyServerOnThisComputer_FinishesWithTheWordsOfTheOwnersRecoveryKey()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-create-server-recovery-").FullName;
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
            var tools = At("tools");
            // The server of this process stands for the one installed here: it runs already, and gives its setup code.
            var processes = new RunningServer(Path.Combine(tools, OperatingSystem.IsWindows() ? "altaserver.exe" : "altaserver"), address, server.SetupCode!.Code);
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog, Processes = processes };

            var events = new List<MachineSetupEvent>();
            await foreach (var value in service.CreateOnThisComputerAsync(new FleetServerLocalRequest(epoch, "Alex", "Home", address.Port, At("data"), tools, null), token))
                events.Add(value);

            var finished = events.Last();
            Assert.AreEqual(("finished", "done"), (finished.Step, finished.State), string.Join(", ", events.Select(static value => $"{value.Step}:{value.State}:{value.Code}")));
            Assert.AreEqual(servers.Connections.Single().Link.Id, finished.MachineId, "the link of the server claimed");
            Assert.AreEqual(32, finished.RecoveryWords?.Split(' ').Length, "the words of the recovery key come once, with the end of the setup");
            Assert.IsTrue(FleetRecoveryKey.TryParse(finished.RecoveryWords, out var key));
            key.Dispose();
            Assert.IsTrue(events.Take(events.Count - 1).All(static value => value.RecoveryWords is null), "only the end of the setup carries them");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // A server installed and running on this computer: its status names the address, and its setup code is the real one.
    private sealed class RunningServer(string executable, Uri address, string setupCode) : ILocalProcesses
    {
        public Task<SshResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            => Task.FromResult(arguments[0] switch
            {
                "status" => new SshResult(0, $"CodeAlta Server 4.0.0, process 1\nListens on {address.ToString().TrimEnd('/')}\nHealth: ok, no owner yet\n", ""),
                "setup-code" => new SshResult(0, setupCode + "\n", ""),
                _ => new SshResult(1, "", "unexpected"),
            });

        public void StartDetached(string executable, IReadOnlyList<string> arguments) => throw new InvalidOperationException("The server runs already.");

        public bool Exists(string path) => string.Equals(path, executable, StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<string> ReadLines(string path) => [];
    }
}
