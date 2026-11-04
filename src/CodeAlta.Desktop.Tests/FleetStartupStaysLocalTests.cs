using System.Collections.Frozen;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Staying local (<c>doc/fleet/security-and-privacy.md</c>, S1): the start of a desktop wires and starts its fleet as the
/// application does (<see cref="DesktopFleetStartup"/>), and a profile that never paired a machine nor joined a server
/// makes no device key and calls no server, while the guard of the policies is asked.
/// </summary>
[TestClass]
public sealed class FleetStartupStaysLocalTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheStartOfADesktopThatNeverPairedMakesNoDeviceKey_AndCallsNoServer()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-local-").FullName;
        try
        {
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = root, CurrentProjectPath = Directory.CreateDirectory(Path.Combine(root, "project")).FullName,
                DiscoveryScope = new(Directory.CreateDirectory(Path.Combine(root, "home")).FullName, root),
                BuiltInSkillRoot = Directory.CreateDirectory(Path.Combine(root, "builtin")).FullName,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            using var network = new CountingHandler();
            var startup = DesktopFleetStartup.Create(host, new SessionHostRegistry(new LocalSessionHost(host)), "desktop", "4.0.0", network);
            await using var fleet = startup.Fleet;
            await using var children = startup.RemoteChildren;
            await using var servers = startup.Servers;
            startup.StartRemoteChildren();
            await startup.StartAsync(token);

            // What an agent asks before it starts work elsewhere: the policies of the servers, of which there are none.
            Assert.IsNull(servers.Policy.Halt);
            Assert.IsNull(servers.Policy.RulesFor(null));

            Assert.AreEqual(0, fleet.Hosts.Count);
            Assert.AreEqual(0, servers.Connections.Count);
            Assert.AreEqual(0, network.Requests, "no server is called");
            Assert.IsNull(servers.DeviceId, "no device key is loaded");
            Assert.AreEqual(0, Directory.EnumerateFiles(root, "device-key.json", SearchOption.AllDirectories).Count(), "no device key is made");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }
}
