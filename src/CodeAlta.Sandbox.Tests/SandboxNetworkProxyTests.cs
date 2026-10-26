using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeAlta.Sandbox.Network;

namespace CodeAlta.Sandbox.Tests;

[TestClass]
public sealed class SandboxNetworkProxyTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task TheProxy_TunnelsALocalPortOfTheProfile_AndRefusesTheOthersWithA403()
    {
        using var upstream = new EchoServer();
        await using var proxy = SandboxNetworkProxy.Start(["nuget.org"], [upstream.Port], [2582]);

        var tunneled = await SendAsync(proxy, $"CONNECT localhost:{upstream.Port} HTTP/1.1\r\nHost: localhost\r\n\r\n", "ping");
        Assert.StartsWith("HTTP/1.1 200", tunneled);
        Assert.EndsWith("ping", tunneled, "the bytes go to the port and back");

        var refused = await SendAsync(proxy, "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com\r\n\r\n", null);
        Assert.StartsWith("HTTP/1.1 403", refused);
        Assert.Contains("example.com:443", refused);
        CollectionAssert.Contains(proxy.RecentRefusals.ToList(), "example.com:443");

        Assert.StartsWith("HTTP/1.1 403", await SendAsync(proxy, "CONNECT localhost:2582 HTTP/1.1\r\n\r\n", null), "CodeAlta's own port is never reached");
        Assert.StartsWith("HTTP/1.1 403", await SendAsync(proxy, "CONNECT 127.0.0.1:445 HTTP/1.1\r\n\r\n", null), "a port of this computer that the profile does not list");
        Assert.StartsWith("HTTP/1.1 403", await SendAsync(proxy, "CONNECT 169.254.169.254:80 HTTP/1.1\r\n\r\n", null), "the metadata of a cloud");
    }

    [TestMethod]
    public async Task APlainHttpRequest_GoesOnInTheFormAnOriginServerReads_AsTheOnlyOneOfItsConnection()
    {
        using var upstream = new EchoServer();
        await using var proxy = SandboxNetworkProxy.Start([], [upstream.Port], []);

        var answer = await SendAsync(proxy,
            $"GET http://localhost:{upstream.Port}/index.json?x=1 HTTP/1.1\r\nHost: localhost\r\nProxy-Connection: keep-alive\r\nConnection: keep-alive\r\nAccept: */*\r\n\r\n", null);

        Assert.StartsWith("GET /index.json?x=1 HTTP/1.1", answer, "the echo shows what the origin received");
        Assert.Contains("Accept: */*\r\n", answer, "the headers of the request go on");
        Assert.Contains("Connection: close\r\n", answer, "a next request opens a connection the proxy reads");
        Assert.DoesNotContain("keep-alive", answer);
    }

    [TestMethod]
    public async Task TheRules_KeepTheAddressesOfThisComputerAndItsNetworksOut()
    {
        IReadOnlySet<int> none = new HashSet<int>();
        foreach (var local in new[] { "127.0.0.1", "10.1.2.3", "192.168.1.10", "172.20.0.1", "169.254.169.254", "100.64.0.1", "::1", "fe80::1", "fd00::1", "0.0.0.0" })
            Assert.IsTrue(SandboxNetworkProxy.IsLocalAddress(IPAddress.Parse(local)), local);
        Assert.IsFalse(SandboxNetworkProxy.IsLocalAddress(IPAddress.Parse("93.184.215.14")));

        Assert.IsNotNull(await SandboxNetworkProxy.ResolveAllowedAsync("93.184.215.14", 443, null, none, none, CancellationToken.None), "the open network reaches the internet");
        Assert.IsNull(await SandboxNetworkProxy.ResolveAllowedAsync("10.0.0.5", 443, null, none, none, CancellationToken.None), "but not the local network");
        Assert.IsNull(await SandboxNetworkProxy.ResolveAllowedAsync("93.184.215.14", 443, ["nuget.org"], none, none, CancellationToken.None), "an address is no host of the list");
        Assert.IsNull(await SandboxNetworkProxy.ResolveAllowedAsync("api.localhost", 80, null, none, none, CancellationToken.None));
    }

    [TestMethod]
    public async Task TheHttpClientOfASandbox_FollowsItsNetwork()
    {
        using var upstream = new EchoServer(http: true);
        var policy = SandboxPolicyBuilder.Build(SandboxProfiles.Resolve("local", [new SandboxProfile { Name = "local", Network = SandboxNetwork.Hosts, LocalPorts = [upstream.Port] }]),
            new SandboxSessionFolders { WorkingDirectory = Path.GetTempPath() },
            new SandboxHost { DataRoot = Path.Combine(Path.GetTempPath(), "codealta-proxy-tests"), HomeDirectory = Path.GetTempPath(), Environment = new Dictionary<string, string>() }, [], "s");
        using var allowed = SandboxHttp.Create(policy, new HashSet<int>());
        Assert.AreEqual("ok", await allowed.GetStringAsync($"http://localhost:{upstream.Port}/").WaitAsync(Deadline));

        using var reserved = SandboxHttp.Create(policy, new HashSet<int> { upstream.Port });
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => reserved.GetStringAsync($"http://localhost:{upstream.Port}/"));
        using var off = SandboxHttp.Create(policy with { Network = SandboxNetwork.Off }, new HashSet<int>());
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => off.GetStringAsync($"http://localhost:{upstream.Port}/"));
    }

    private static async Task<string> SendAsync(SandboxNetworkProxy proxy, string request, string? then)
    {
        var endpoint = new Uri(proxy.Endpoint);
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port).WaitAsync(Deadline);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var text = new StringBuilder();
        var buffer = new byte[4096];
        int read;
        if (then is not null)
        {
            read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Deadline);
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(then));
            read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Deadline);
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            return text.ToString();
        }

        // A refusal and an echoed request: read until the other side closes, or until the echo has its blank line.
        while ((read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Deadline)) > 0)
        {
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (!text.ToString().StartsWith("HTTP/", StringComparison.Ordinal) && text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        return text.ToString();
    }

    // A loopback server that echoes what it receives, or answers one HTTP request with "ok".
    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public EchoServer(bool http = false)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        using var _ = client;
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        try
                        {
                            int read;
                            while ((read = await stream.ReadAsync(buffer)) > 0)
                            {
                                if (http)
                                {
                                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
                                    return;
                                }

                                await stream.WriteAsync(buffer.AsMemory(0, read));
                            }
                        }
                        catch (IOException)
                        {
                        }
                    });
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose() => _listener.Stop();
    }
}
