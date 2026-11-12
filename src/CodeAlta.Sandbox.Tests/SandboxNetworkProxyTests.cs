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
    public async Task AHostItLetsThrough_IsReachedOverTheUpstreamProxy_AtTheAddressItChecked_SignedInWhenAsked()
    {
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(@"CORP\jdoe:p:w"));
        using var corporate = new UpstreamProxy(basic);
        var upstream = new FixedProxy(new Uri($"http://127.0.0.1:{corporate.Port}"), ["git.corp.example"]) { Credentials = new NetworkCredential("jdoe", "p:w", "CORP") };
        // localhost is the one name a test resolves without a network: its address is the loopback, which a profile with the port reaches.
        await using var proxy = SandboxNetworkProxy.Start(null, [4444], [], upstream);

        var tunneled = await SendAsync(proxy, "CONNECT localhost:4444 HTTP/1.1\r\nHost: localhost\r\n\r\n", "ping");
        Assert.StartsWith("HTTP/1.1 200", tunneled);
        Assert.EndsWith("ping", tunneled, "the tunnel of the upstream carries the bytes");
        var (first, second) = (corporate.Requests[0], corporate.Requests[1]);
        // The upstream connects to the address the sandbox checked, never to one it would resolve itself.
        Assert.StartsWith("CONNECT 127.0.0.1:4444 HTTP/1.1\r\nHost: 127.0.0.1:4444\r\n", first);
        Assert.DoesNotContain("Proxy-Authorization", first, "nothing is sent before the proxy asks");
        Assert.Contains($"Proxy-Authorization: {basic}\r\n", second, "the challenge is answered");

        // A plain request goes to the upstream in the absolute form, at the address checked, with the name of the host it asked for.
        var plain = await SendAsync(proxy, "GET http://localhost:4444/x HTTP/1.1\r\nHost: localhost:4444\r\nAccept: */*\r\n\r\n", null);
        Assert.StartsWith("HTTP/1.1 200", plain);
        Assert.Contains("GET http://127.0.0.1:4444/x HTTP/1.1\r\n", plain);
        Assert.Contains("Host: localhost:4444\r\n", plain);
        Assert.Contains($"Proxy-Authorization: {basic}\r\n", plain);
        Assert.Contains("Connection: close\r\n", plain);

        // The rules of the sandbox come first: a refused host never reaches the upstream.
        var count = corporate.Requests.Count;
        Assert.StartsWith("HTTP/1.1 403", await SendAsync(proxy, "CONNECT 10.0.0.5:443 HTTP/1.1\r\n\r\n", null));
        Assert.AreEqual(count, corporate.Requests.Count);
    }

    [TestMethod]
    public async Task WhatTheUpstreamAsks_AndIsNotGiven_IsWhatTheSandboxGets()
    {
        using var corporate = new UpstreamProxy("Basic expected");
        // No credentials, then the account of the process, which is never sent over Basic.
        foreach (var credentials in new ICredentials?[] { null, CredentialCache.DefaultNetworkCredentials })
        {
            await using var proxy = SandboxNetworkProxy.Start(null, [4444], [], new FixedProxy(new Uri($"http://127.0.0.1:{corporate.Port}"), []) { Credentials = credentials });
            var refused = await SendAsync(proxy, "CONNECT localhost:4444 HTTP/1.1\r\n\r\n", null);
            Assert.StartsWith("HTTP/1.1 407", refused);
            Assert.Contains("Proxy-Authenticate: Basic realm=\"corp\"", refused, "the sandbox sees what the proxy asks");
            Assert.Contains("X-CodeAlta-Sandbox: upstream", refused);
        }

        Assert.IsTrue(corporate.Requests.All(static request => !request.Contains("Proxy-Authorization", StringComparison.Ordinal)), "nothing sent but what was asked");
    }

    private sealed class FixedProxy(Uri address, string[] bypassed) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : address;

        public bool IsBypassed(Uri host) => bypassed.Contains(host.Host, StringComparer.OrdinalIgnoreCase);
    }

    // A loopback proxy that records the head of each request, and asks Basic credentials when it has some to expect: it
    // answers a CONNECT with 200 and then echoes, and a plain request with its head as the body of a 200.
    private sealed class UpstreamProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _requests = new();

        public UpstreamProxy(string? authorization)
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
                            while (true)
                            {
                                var head = new StringBuilder();
                                var one = new byte[1];
                                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                                {
                                    if (await stream.ReadAsync(one) == 0) return;
                                    head.Append((char)one[0]);
                                }

                                var text = head.ToString();
                                _requests.Enqueue(text);
                                if (authorization is not null && !text.Contains($"Proxy-Authorization: {authorization}\r\n", StringComparison.Ordinal))
                                {
                                    // The connection stays for the answer to the challenge.
                                    await stream.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"corp\"\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
                                    continue;
                                }

                                if (!text.StartsWith("CONNECT", StringComparison.Ordinal))
                                {
                                    var body = Encoding.ASCII.GetBytes(text);
                                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                                    await stream.WriteAsync(body);
                                    return;
                                }

                                await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray());
                                var buffer = new byte[4096];
                                int read;
                                while ((read = await stream.ReadAsync(buffer)) > 0) await stream.WriteAsync(buffer.AsMemory(0, read));
                                return;
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
