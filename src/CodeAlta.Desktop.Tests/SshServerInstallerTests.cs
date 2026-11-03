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
            DataRoot = "~/srv/data", ToolPath = "~/srv/tools" }, (address, _) => { checkedAddress = address; return Task.FromResult(true); },
            (address, setup, code, _) => { claimed = (address, setup, code); return Task.FromResult("link-1"); }, steps.Add, TestContext.CancellationToken);

        Assert.AreEqual("link-1", link);
        Assert.AreEqual(new Uri("http://box.home.arpa:2693"), checkedAddress, "the address the machines use is checked from this computer before the claim");
        Assert.AreEqual((new Uri("http://box.home.arpa:2693"), new Uri("http://127.0.0.1:42693"), "WXYZ-1234"), claimed,
            "the link keeps the address of the machines; the setup goes through the forward, from the loopback of the server");
        Assert.AreEqual(1, ssh.Forwards);
        StringAssert.Contains(ssh.Scripts[1], "tool update --tool-path \"$TOOLS\" CodeAlta.Server --version '4.0.0' --allow-downgrade");
        StringAssert.Contains(ssh.Scripts[2], "URLS=http://0.0.0.0:2693");
        StringAssert.Contains(ssh.Scripts[2], "systemctl --user enable --now \"$UNIT\"");
        StringAssert.Contains(ssh.Scripts[2], "loginctl enable-linger");
        StringAssert.Contains(ssh.Scripts[3], "setup-code --data-root \"$ROOT\"");
        CollectionAssert.AreEqual(new[] { "connect", "check", "install", "service", "reach", "claim" },
            steps.Where(static step => step.State == "done").Select(static step => step.Step).ToArray());
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
            new SshServerSetupRequest { Target = new SshTarget("me", "box", null) }, (_, _) => Task.FromResult(false),
            (_, _, _, _) => { claims++; return Task.FromResult("x"); }, static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual(("reach", "server_blocked_ufw"), (failure.Step, failure.Code));
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
            new SshServerSetupRequest { Target = new SshTarget("me", "box", null), Address = new Uri("https://fleet.example.com") }, (_, _) => Task.FromResult(false),
            (_, _, _, _) => Task.FromResult("x"), static _ => { }, TestContext.CancellationToken));
        StringAssert.Contains(ssh.Scripts[2], "URLS=http://127.0.0.1:2586");
        Assert.AreEqual("server_proxy_unreachable", failure.Code, "what stands in front of the server does not answer");
    }
}
