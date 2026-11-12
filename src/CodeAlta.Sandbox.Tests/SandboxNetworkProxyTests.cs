using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeAlta.Sandbox.Network;

namespace CodeAlta.Sandbox.Tests;

[TestClass]
public sealed class SandboxNetworkProxyTests
{
    private static readonly TimeSpan Deadline = TestTime.Seconds(20);

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
    public async Task AProxyDisposedAsSoonAsItStarts_EndsWithoutThrowing()
    {
        // A worker that fails to start disposes its proxy at once. Its listener stops only once the accept loop ended:
        // a loop that ran after the stop would get "Not listening" in the place of the failure of the start.
        for (var index = 0; index < 500; index++)
        {
            var proxy = SandboxNetworkProxy.Start([], [], []);
            await proxy.DisposeAsync().AsTask().WithinAsync(Deadline);
        }
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
        Assert.AreEqual("ok", await allowed.GetStringAsync($"http://localhost:{upstream.Port}/").WithinAsync(Deadline));

        using var reserved = SandboxHttp.Create(policy, new HashSet<int> { upstream.Port });
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => reserved.GetStringAsync($"http://localhost:{upstream.Port}/"));
        using var off = SandboxHttp.Create(policy with { Network = SandboxNetwork.Off }, new HashSet<int>());
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => off.GetStringAsync($"http://localhost:{upstream.Port}/"));
    }

    [TestMethod]
    public async Task AHostItLetsThrough_IsReachedOverTheUpstreamProxy_WithItsCredentials()
    {
        using var corporate = new UpstreamProxy(answer: "HTTP/1.1 200 Connection established");
        var upstream = new FixedProxy(new Uri($"http://127.0.0.1:{corporate.Port}"), ["git.corp.example"]) { Credentials = new NetworkCredential("jdoe", "p:w", "CORP") };
        // An address of the internet, written as an address: nothing is resolved, and the upstream is the only server.
        await using var proxy = SandboxNetworkProxy.Start(null, [], [], upstream);

        var tunneled = await SendAsync(proxy, "CONNECT 93.184.215.14:443 HTTP/1.1\r\nHost: 93.184.215.14\r\n\r\n", "ping");
        Assert.StartsWith("HTTP/1.1 200", tunneled);
        Assert.EndsWith("ping", tunneled, "the tunnel of the upstream carries the bytes");
        var connect = corporate.Requests.Single();
        Assert.StartsWith("CONNECT 93.184.215.14:443 HTTP/1.1\r\nHost: 93.184.215.14:443\r\n", connect);
        Assert.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(@"CORP\jdoe:p:w")) + "\r\n", connect);

        // A plain request goes to the upstream in the absolute form, which it needs to reach the host.
        var plain = await SendAsync(proxy, "GET http://93.184.215.14/x HTTP/1.1\r\nHost: 93.184.215.14\r\nAccept: */*\r\n\r\n", null);
        Assert.StartsWith("GET http://93.184.215.14/x HTTP/1.1\r\n", plain);
        Assert.Contains("Proxy-Authorization: Basic ", plain);
        Assert.Contains("Connection: close\r\n", plain);

        // The rules of the sandbox come first: a refused host never reaches the upstream.
        Assert.StartsWith("HTTP/1.1 403", await SendAsync(proxy, "CONNECT 10.0.0.5:443 HTTP/1.1\r\n\r\n", null));
        Assert.AreEqual(2, corporate.Requests.Count);
    }

    [TestMethod]
    public async Task WhatTheUpstreamRefuses_IsWhatTheSandboxGets()
    {
        using var corporate = new UpstreamProxy(answer: "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"corp\"");
        await using var proxy = SandboxNetworkProxy.Start(null, [], [], new FixedProxy(new Uri($"http://127.0.0.1:{corporate.Port}"), []));
        var refused = await SendAsync(proxy, "CONNECT 93.184.215.14:443 HTTP/1.1\r\n\r\n", null);
        Assert.StartsWith("HTTP/1.1 407", refused);
        Assert.DoesNotContain("Proxy-Authorization", corporate.Requests.Single(), "no credentials, none sent");
    }

    private sealed class FixedProxy(Uri address, string[] bypassed) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : address;

        public bool IsBypassed(Uri host) => bypassed.Contains(host.Host, StringComparer.OrdinalIgnoreCase);
    }

    // A loopback proxy that records the head of each request; it answers a CONNECT with its answer and then echoes, and a
    // plain request with the request itself.
    private sealed class UpstreamProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _requests = new();

        public UpstreamProxy(string answer)
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
                        try
                        {
                            var head = new StringBuilder();
                            var one = new byte[1];
                            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) > 0) head.Append((char)one[0]);
                            _requests.Enqueue(head.ToString());
                            if (!head.ToString().StartsWith("CONNECT", StringComparison.Ordinal))
                            {
                                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
                                return;
                            }

                            await stream.WriteAsync(Encoding.ASCII.GetBytes(answer + "\r\n\r\n"));
                            if (!answer.Contains(" 200 ", StringComparison.Ordinal)) return;
                            var buffer = new byte[4096];
                            int read;
                            while ((read = await stream.ReadAsync(buffer)) > 0) await stream.WriteAsync(buffer.AsMemory(0, read));
                        }
                        catch (IOException)
                        {
                        }
                    });
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyList<string> Requests => [.. _requests];

        public void Dispose() => _listener.Stop();
    }

    private static async Task<string> SendAsync(SandboxNetworkProxy proxy, string request, string? then)
    {
        var endpoint = new Uri(proxy.Endpoint);
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port).WithinAsync(Deadline);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var text = new StringBuilder();
        var buffer = new byte[4096];
        int read;
        if (then is not null)
        {
            read = await stream.ReadAsync(buffer).AsTask().WithinAsync(Deadline);
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(then));
            read = await stream.ReadAsync(buffer).AsTask().WithinAsync(Deadline);
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            return text.ToString();
        }

        // A refusal and an echoed request: read until the other side closes, or until the echo has its blank line.
        while ((read = await stream.ReadAsync(buffer).AsTask().WithinAsync(Deadline)) > 0)
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
