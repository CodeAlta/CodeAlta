using CodeAlta.Desktop.Fleet.MachineSetup;

namespace CodeAlta.Desktop.Tests;

/// <summary>Create my server on a machine over SSH (<see cref="SshServerInstaller"/>) against a scripted ssh: its steps, its service, the reach and the claim through a forward.</summary>
[TestClass]
public sealed class SshServerInstallerTests
{
    private const string Ubuntu = "os=Linux\narch=x86_64\nhome=/home/me\nuser=me\nhostname=box\ndotnet=/usr/bin/dotnet\nsdk=10.0.114 [/usr/lib/dotnet/sdk]\n"
        + "runtime=Microsoft.NETCore.App 10.0.14 [x]\nlinger=no\nfirewall=ufw\n";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OnTheLocalNetwork_TheServerListensEverywhere_IsCheckedFromHere_ThenClaimedThroughAForward()
    {
        var ssh = new SshNodeInstallerTests.ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Ubuntu, ""));
        ssh.Answers.Enqueue(new SshResult(0, "4.0.0\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        ssh.Answers.Enqueue(new SshResult(0, "WXYZ-1234\nValid until 16:00, once, from this machine only.\n", ""));
        Uri? checkedAddress = null;
        (Uri Link, Uri Setup, string Code)? claimed = null;
        var steps = new List<SshSetupProgress>();

        var link = await new SshServerInstaller(ssh, "4.0.0+abc").RunAsync(new SshServerSetupRequest { Target = new SshTarget("me", "box.home.arpa", null), Port = 2693,
            DataRoot = "~/srv/data", ToolPath = "~/srv/tools" }, (address, _, _) => { checkedAddress = address; return Task.FromResult<ReachProblem?>(null); },
            (address, setup, code, _, _) => { claimed = (address, setup, code); return Task.FromResult("link-1"); }, steps.Add, TestContext.CancellationToken);

        Assert.AreEqual("link-1", link);
        Assert.AreEqual(new Uri("http://box.home.arpa:2693"), checkedAddress, "the address the machines use is checked from this computer before the claim");
        Assert.AreEqual((new Uri("http://box.home.arpa:2693"), new Uri("http://127.0.0.1:42693"), "WXYZ-1234"), claimed,
            "the link keeps the address of the machines; the setup goes through the forward, from the loopback of the server");
        Assert.AreEqual(1, ssh.Forwards);
        StringAssert.Contains(ssh.Scripts[1], "tool update --tool-path \"$TOOLS\" CodeAlta.Server --version '4.0.0' --allow-downgrade");
        // The service is altaserver install's, as the owner would install it there: the desktop writes no unit of its own.
        StringAssert.Contains(ssh.Scripts[2], "\"$TOOLS/altaserver\" install --data-root \"$ROOT\" --urls http://0.0.0.0:2693 --allow-insecure-http 1>&2\n");
        Assert.IsFalse(ssh.Scripts[2].Contains("[Service]", StringComparison.Ordinal), ssh.Scripts[2]);
        Assert.IsFalse(ssh.Scripts[2].Contains("systemctl --user enable", StringComparison.Ordinal), ssh.Scripts[2]);
        StringAssert.Contains(ssh.Scripts[2], "status --data-root \"$ROOT\" 2>/dev/null | grep -q '^Health: ok'", "it waits until the server answers, not until status prints a health line");
        StringAssert.Contains(ssh.Scripts[3], "setup-code --data-root \"$ROOT\"");
        CollectionAssert.AreEqual(new[] { "connect", "check", "install", "service", "reach", "claim" },
            steps.Where(static step => step.State == "done").Select(static step => step.Step).ToArray());
    }

    [TestMethod]
    public async Task OnTheLocalNetworkEncrypted_TheServerMakesItsOwnCertificate_WhosePinTheSetupReadsOverSsh_AndEveryStepChecks()
    {
        const string Pin = "sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var ssh = new SshNodeInstallerTests.ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Ubuntu, ""));
        ssh.Answers.Enqueue(new SshResult(0, "4.0.0\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, Pin + "\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, "WXYZ-1234\n", ""));
        (Uri Address, string? Pin)? checkedAt = null;
        (Uri Link, Uri Setup, string? Pin)? claimed = null;

        await new SshServerInstaller(ssh, "4.0.0").RunAsync(new SshServerSetupRequest { Target = new SshTarget("me", "box", null), Port = 2693, OwnCertificate = true },
            (address, pin, _) => { checkedAt = (address, pin); return Task.FromResult<ReachProblem?>(null); },
            (address, setup, _, pin, _) => { claimed = (address, setup, pin); return Task.FromResult("link-1"); }, static _ => { }, TestContext.CancellationToken);

        StringAssert.Contains(ssh.Scripts[2], "\"$TOOLS/altaserver\" install --urls https://0.0.0.0:2693 --own-certificate 1>&2\n");
        Assert.IsFalse(ssh.Scripts[2].Contains("--allow-insecure-http", StringComparison.Ordinal), "nothing goes in clear on the network");
        StringAssert.Contains(ssh.Scripts[2], "sed -n 's/^Certificate: //p'", "the pin comes over SSH, the channel this computer trusts already");
        Assert.AreEqual((new Uri("https://box:2693"), (string?)Pin), checkedAt, "the address of the machines is checked with the pin, as each device will");
        Assert.AreEqual((new Uri("https://box:2693"), new Uri("https://127.0.0.1:42693"), (string?)Pin), claimed, "the claim through the forward checks it too, and the link keeps it");
    }

    // Always on: on macOS a LaunchDaemon run as the person replaces the LaunchAgent; the step says what happens after a restart.
    [TestMethod]
    public async Task AlwaysOn_OnMacOS_TheServerIsALaunchDaemonOfThePerson_AndThePinIsReadBeforeItsStatus()
    {
        const string Pin = "sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string Mac = "os=Darwin\narch=arm64\nhome=/Users/me\nuser=me\nhostname=mac\ndotnet=/usr/local/share/dotnet/dotnet\nsdk=10.0.402 [x]\nsudo=nopassword\n";
        var ssh = new SshNodeInstallerTests.ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Mac, ""));
        ssh.Answers.Enqueue(new SshResult(0, "4.1.0\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, Pin + "\n--- altanode status\nCertificate: sha256:BBBB\nAfter a restart: it starts at boot as me, without a login.\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, "WXYZ-1234\n", ""));
        var steps = new List<SshSetupProgress>();
        string? checkedPin = null;

        await new SshServerInstaller(ssh, "4.1.0").RunAsync(new SshServerSetupRequest { Target = new SshTarget("me", "mac", null), OwnCertificate = true, AlwaysOn = true },
            (_, pin, _) => { checkedPin = pin; return Task.FromResult<ReachProblem?>(null); }, (_, _, _, _, _) => Task.FromResult("link-1"), steps.Add, TestContext.CancellationToken);

        Assert.AreEqual(Pin, checkedPin);
        StringAssert.Contains(ssh.Scripts[2], "\"$TOOLS/altaserver\" uninstall >/dev/null 2>&1 || true\n");
        StringAssert.Contains(ssh.Scripts[2], "sudo -n env \"DOTNET_ROOT=$DOTNET_ROOT\" \"PATH=$PATH\" \"$TOOLS/altaserver\" install --system --account \"$ME\" --urls https://0.0.0.0:2586 --own-certificate 1>&2\n");
        StringAssert.EndsWith(steps.Single(static step => step is { Step: "service", State: "done" }).Detail, "· After a restart: it starts at boot as me, without a login.");

        // Without sudo there, the check says so with the command to run.
        var refused = new SshNodeInstallerTests.ScriptedSsh();
        refused.Answers.Enqueue(new SshResult(0, Mac.Replace("sudo=nopassword\n", "", StringComparison.Ordinal), ""));
        var missing = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshServerInstaller(refused, "4.1.0").RunAsync(
            new SshServerSetupRequest { Target = new SshTarget("me", "mac", null), AlwaysOn = true }, (_, _, _) => Task.FromResult<ReachProblem?>(null),
            (_, _, _, _, _) => Task.FromResult("l"), static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual(("sudo_missing", "sudo altaserver install --system --account me"), (missing.Code, missing.Message));
    }

    [TestMethod]
    public async Task AServerWithItsOwnCertificateThatPrintsNoPin_IsNotClaimed()
    {
        var ssh = new SshNodeInstallerTests.ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Ubuntu, ""));
        ssh.Answers.Enqueue(new SshResult(0, "4.0.0\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        var failure = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshServerInstaller(ssh, "4.0.0").RunAsync(
            new SshServerSetupRequest { Target = new SshTarget("me", "box", null), OwnCertificate = true }, (_, _, _) => Task.FromResult<ReachProblem?>(null),
            (_, _, _, _, _) => Task.FromResult("x"), static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual(("service", "service_failed"), (failure.Step, failure.Code));
        Assert.AreEqual(0, ssh.Forwards);
    }

    [TestMethod]
    public async Task AServerThatDoesNotAnswerFromHere_IsNotClaimed_AndTheFirewallIsNamed()
    {
        var ssh = new SshNodeInstallerTests.ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Ubuntu, ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        var claims = 0;
        var failure = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshServerInstaller(ssh, "4.0.0").RunAsync(
            new SshServerSetupRequest { Target = new SshTarget("me", "box", null) }, (_, _, _) => Task.FromResult<ReachProblem?>(new("No route to host")),
            (_, _, _, _, _) => { claims++; return Task.FromResult("x"); }, static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual(("reach", "server_blocked_ufw"), (failure.Step, failure.Code));
        StringAssert.Contains(failure.Message, "No route to host", "the person sees what this computer met, not only the port");
        Assert.AreEqual(0, claims);
        Assert.AreEqual(0, ssh.Forwards);
    }

    [TestMethod]
    public async Task BehindAnAddressOfItsOwn_TheServerStaysOnItsLoopback()
    {
        var ssh = new SshNodeInstallerTests.ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Ubuntu.Replace("firewall=ufw\n", "", StringComparison.Ordinal), ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        var failure = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshServerInstaller(ssh, "4.0.0").RunAsync(
            new SshServerSetupRequest { Target = new SshTarget("me", "box", null), Address = new Uri("https://fleet.example.com") }, (_, _, _) => Task.FromResult<ReachProblem?>(new("No route to host")),
            (_, _, _, _, _) => Task.FromResult("x"), static _ => { }, TestContext.CancellationToken));
        StringAssert.Contains(ssh.Scripts[2], "\"$TOOLS/altaserver\" install --urls http://127.0.0.1:2586 --trusted-proxy '127.0.0.1;::1' 1>&2\n",
            "the default data root: the service of ~/.altaserver, which trusts the proxy on its loopback");
        Assert.AreEqual("server_proxy_unreachable", failure.Code, "what stands in front of the server does not answer");
    }

    [TestMethod]
    public async Task BehindAnAddressOfItsOwn_ThePasskeyPageIsAtTheHttpsAddressTheProxyOnItsLoopbackServes()
    {
        var token = TestContext.CancellationToken;
        var ssh = new SshNodeInstallerTests.ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Ubuntu, ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshServerInstaller(ssh, "4.0.0").RunAsync(
            new SshServerSetupRequest { Target = new SshTarget("me", "box", null), Address = new Uri("https://fleet.example.com") }, (_, _, _) => Task.FromResult<ReachProblem?>(new("stop here")),
            (_, _, _, _, _) => Task.FromResult("x"), static _ => { }, token));

        // The server runs with what the installation gave altaserver: its proxies, as altaserver reads them.
        var given = System.Text.RegularExpressions.Regex.Match(ssh.Scripts[2], "--trusted-proxy '([^']*)'");
        Assert.IsTrue(given.Success, ssh.Scripts[2]);
        var root = Directory.CreateTempSubdirectory("codealta-proxy-install-").FullName;
        try
        {
            async Task<string> PasskeyPageAsync(IReadOnlyList<string> proxies, string name)
            {
                await using var server = await CodeAlta.Server.Hosting.CoordinatorServer.StartAsync(new CodeAlta.Server.Hosting.CoordinatorServerOptions
                {
                    DataRoot = Path.Combine(root, name), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null, TrustedProxies = proxies,
                }, token);
                var address = server.Addresses.First(static uri => uri.Scheme == "http");
                using var ownerKey = CodeAlta.Fleet.Identity.DeviceIdentity.LoadOrCreate(Path.Combine(root, name + "-owner"));
                using (var owner = new CodeAlta.Fleet.Server.FleetServerClient(address, ownerKey)) await owner.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", null, token);
                // A new computer reaches the server through the proxy, which ended TLS and says so.
                using var key = CodeAlta.Fleet.Identity.DeviceIdentity.LoadOrCreate(Path.Combine(root, name + "-laptop"));
                using var client = new CodeAlta.Fleet.Server.FleetServerClient(address, key, new ForwardedProto(), null, null);
                return (await client.StartSignInAsync(CodeAlta.Fleet.Protocol.Api.FleetSignInMethods.Passkey, "http://127.0.0.1:53682/callback",
                    CodeAlta.Fleet.Protocol.Api.FleetSignInPurposes.Enroll, token)).AuthorizationUrl;
            }

            StringAssert.StartsWith(await PasskeyPageAsync(given.Groups[1].Value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), "installed"),
                "https://", "the scheme the proxy on the loopback forwards");
            StringAssert.StartsWith(await PasskeyPageAsync([], "untrusted"), "http://", "without the trust the installation gives, the page would be at http://");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // What Tailscale Serve or Caddy on the server's machine adds once it ended TLS.
    private sealed class ForwardedProto() : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            return base.SendAsync(request, cancellationToken);
        }
    }

    [TestMethod]
    public async Task ARefusedSignIn_SaysWhatSshSaid_WithoutTheNoticeOfAHostKeyItJustRecorded()
    {
        var ssh = new SshNodeInstallerTests.ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(SshResult.ConnectionFailed, "",
            "Warning: Permanently added 'box' (ED25519) to the list of known hosts.\r\nme@box: Permission denied (publickey).\r\n"));
        var failure = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshServerInstaller(ssh, "4.0.0").RunAsync(
            new SshServerSetupRequest { Target = new SshTarget("me", "box", null), TrustedHostKey = "SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)" }, (_, _, _) => Task.FromResult<ReachProblem?>(null),
            (_, _, _, _, _) => Task.FromResult("x"), static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual(("connect", "auth_failed"), (failure.Step, failure.Code));
        Assert.AreEqual("me@box: Permission denied (publickey).", failure.Message);
    }

    [TestMethod]
    public async Task AServerThisComputerIsKeptFrom_SaysSo_InsteadOfBlamingTheFirewallOfTheMachine()
    {
        var ssh = new SshNodeInstallerTests.ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Ubuntu, ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        var failure = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshServerInstaller(ssh, "4.0.0").RunAsync(
            new SshServerSetupRequest { Target = new SshTarget("me", "box", null) }, (_, _, _) => Task.FromResult<ReachProblem?>(new("No route to host", BlockedHere: true)),
            (_, _, _, _, _) => Task.FromResult("x"), static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual(("reach", "local_network_blocked"), (failure.Step, failure.Code), "macOS keeps the application from the local network, not ufw on the machine");
        Assert.AreEqual("http://box:2586 · No route to host", failure.Message);
    }

    [TestMethod]
    public void NoRouteToHost_IsTheRefusalOfMacOS_OnlyOnMacOS()
    {
        var refused = new HttpRequestException("No route to host (box:2586)", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable));
        Assert.IsTrue(LocalNetworkAccess.IsBlockedHere(refused, isMacOS: true));
        Assert.IsTrue(LocalNetworkAccess.ProblemOf(refused, isMacOS: true).BlockedHere);
        Assert.IsFalse(LocalNetworkAccess.IsBlockedHere(refused, isMacOS: false), "elsewhere it is a real lack of route");
        var closed = new HttpRequestException("Connection refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        Assert.IsFalse(LocalNetworkAccess.IsBlockedHere(closed, isMacOS: true), "a port that refuses is the machine's answer");
        Assert.IsFalse(LocalNetworkAccess.ProblemOf(closed, isMacOS: true).BlockedHere);
        // The connection to a node tries each of its addresses: the loopback of this computer refuses, its network has no route.
        var node = new IOException("No address of the machine answered: …", new AggregateException(
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable),
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
        Assert.IsTrue(LocalNetworkAccess.IsBlockedHere(node, isMacOS: true), "the refusal of this computer's own loopback does not hide it");
    }
}
