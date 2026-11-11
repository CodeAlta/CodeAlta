using System.Collections.Immutable;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Fleet.MachineSetup;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A machine paired with this computer joins the servers of this computer it is not on yet: at the end of a setup over SSH
/// (<c>altanode join</c> on the machine, then the approval of the key pairing pinned), or from its page, so that the person's
/// other computers of that server see it too.
/// </summary>
[TestClass]
public sealed class MachineServerJoinTests
{
    private const string Ubuntu = """
        os=Linux
        arch=x86_64
        home=/home/me
        user=me
        hostname=pc-ai
        dotnet=/usr/bin/dotnet
        sdk=10.0.114 [/usr/lib/dotnet/sdk]
        runtime=Microsoft.NETCore.App 10.0.14 [/usr/lib/dotnet/shared/Microsoft.NETCore.App]
        linger=yes
        """;

    private const string Link = "codealta-pair://v1/abc";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Setup_WithAServer_JoinsItFromTheMachine_ThenApprovesTheKeyItPaired()
    {
        var ssh = Paired(new SshResult(0, "Enrolled pc-ai in https://home.example:2586 as pc-ai; it waits for its approval.\n", ""));
        var steps = new List<SshSetupProgress>();
        string? askedFor = null;
        var approvals = 0;

        var machine = await new SshNodeInstaller(ssh, "4.2.0").RunAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "pc-ai", null), Name = "pc-ai" },
            (_, _) => Task.FromResult("machine-1"),
            (machineId, _) =>
            {
                askedFor = machineId;
                return Task.FromResult<IReadOnlyList<SshServerJoin>>([new SshServerJoin("Home", "https://home.example:2586#pin=sha256:AB", "it's-one-time", _ => { approvals++; return Task.CompletedTask; })]);
            }, steps.Add, TestContext.CancellationToken);

        Assert.AreEqual("machine-1", machine);
        Assert.AreEqual("machine-1", askedFor, "the servers are those the paired machine is not on yet");
        Assert.AreEqual(1, approvals, "this computer approves the key it paired with once the node enrolled");
        CollectionAssert.AreEqual(new[] { "pair:running", "pair:done", "server:running", "server:done" },
            steps.SkipWhile(static step => step.Step != "pair").Select(static step => $"{step.Step}:{step.State}").ToArray());
        Assert.AreEqual("Home", steps.Last().Detail);
        StringAssert.Contains(ssh.Scripts[4], "\"$TOOLS/altanode\" join 'https://home.example:2586#pin=sha256:AB' --token 'it'\\''s-one-time' --no-wait --name 'pc-ai'",
            "the node enrolls from itself, without waiting for the approval this computer gives next");
    }

    [TestMethod]
    public async Task Setup_WhenTheServerRefusesTheJoin_LeavesTheMachinePaired_AndSaysWhy()
    {
        var ssh = Paired(new SshResult(1, "", "altanode join: the server refused the enrollment (token_used): The token was used."));
        var steps = new List<SshSetupProgress>();
        var approvals = 0;

        var machine = await new SshNodeInstaller(ssh, "4.2.0").RunAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "pc-ai", null) },
            (_, _) => Task.FromResult("machine-1"),
            (_, _) => Task.FromResult<IReadOnlyList<SshServerJoin>>([new SshServerJoin("Home", "https://home.example", "t", _ => { approvals++; return Task.CompletedTask; })]),
            steps.Add, TestContext.CancellationToken);

        Assert.AreEqual("machine-1", machine, "the setup goes on: the machine is paired");
        Assert.AreEqual(0, approvals, "nothing enrolled, nothing approved");
        var failed = steps.Single(static step => step is { Step: "server", State: "failed" });
        Assert.AreEqual("server_join_failed", failed.Code);
        StringAssert.Contains(failed.Detail, "Home: altanode join: the server refused the enrollment (token_used)");
        Assert.IsFalse(steps.Any(static step => step.State == "failed" && step.Step != "server"));
    }

    [TestMethod]
    public async Task Setup_WhenTheApprovalFails_SaysWhy()
    {
        var steps = new List<SshSetupProgress>();
        await new SshNodeInstaller(Paired(new SshResult(0, "", "")), "4.2.0").RunAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "pc-ai", null) },
            (_, _) => Task.FromResult("machine-1"),
            (_, _) => Task.FromResult<IReadOnlyList<SshServerJoin>>([new SshServerJoin("Home", "https://home.example", "t",
                _ => Task.FromException(new HttpRequestException("The server does not answer.")))]),
            steps.Add, TestContext.CancellationToken);

        var failed = steps.Single(static step => step is { Step: "server", State: "failed" });
        Assert.AreEqual("server_approve_failed", failed.Code);
        Assert.AreEqual("Home: The server does not answer.", failed.Detail);
    }

    [TestMethod]
    public async Task Setup_WithoutAServerToJoin_HasNoServerStep()
    {
        var ssh = Paired();
        var steps = new List<SshSetupProgress>();
        await new SshNodeInstaller(ssh, "4.2.0").RunAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "pc-ai", null) },
            (_, _) => Task.FromResult("machine-1"), (_, _) => Task.FromResult<IReadOnlyList<SshServerJoin>>([]), steps.Add, TestContext.CancellationToken);

        Assert.IsFalse(steps.Any(static step => step.Step == "server"));
        Assert.HasCount(4, ssh.Scripts, "no join runs on the machine");
    }

    [TestMethod]
    public void AServerIsOffered_WhenThisComputerApprovesThereAndTheMachineIsNotOnItYet()
    {
        var link = new FleetServerLink("home", "https://home.example", "org", "Home", "laptop", FleetDeviceKinds.Controller, FleetServerLinkStates.Approved,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var devices = FleetDeviceListState.Empty with
        {
            Members = ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[] { Member("laptop", FleetDeviceKinds.Controller), Member("mini", FleetDeviceKinds.Node) }),
            Removed = ["old-box"],
        };

        Assert.IsTrue(DesktopFleetServers.CanJoin(link, devices, "pc-ai"), "a machine paired here and not on the server");
        Assert.IsFalse(DesktopFleetServers.CanJoin(link, devices, "mini"), "a machine on it already");
        Assert.IsFalse(DesktopFleetServers.CanJoin(link, devices, "old-box"), "a machine the server removed is not offered again");
        Assert.IsFalse(DesktopFleetServers.CanJoin(link, null, "pc-ai"), "not before the device list verified");
        Assert.IsFalse(DesktopFleetServers.CanJoin(link with { State = FleetServerLinkStates.Pending }, devices, "pc-ai"), "this computer waits for its own approval");
        Assert.IsFalse(DesktopFleetServers.CanJoin(link with { Kind = FleetDeviceKinds.Node }, devices, "pc-ai"), "a node approves nothing");
    }

    // The server sees a node that cannot connect only as offline: the page of the machine says why, as the node says it.
    [TestMethod]
    public void ThePageOfAMachineSaysHowItsConnectionToEachServerStands()
    {
        static MachineServerLinkView Of(string link, string? connection, string? message = null, string? organization = "Home")
            => MachineServerLinkView.Of(new FleetNodeServerStatus("https://home.example", organization, link, connection, message, null));

        Assert.AreEqual(new MachineServerLinkView("Home", "connected", null), Of("approved", "Online"));
        Assert.AreEqual(new MachineServerLinkView("Home", "disconnected", "Could not load file or assembly 'System.Net.WebSockets.Client'."),
            Of("approved", "Offline", "Could not load file or assembly 'System.Net.WebSockets.Client'."));
        Assert.AreEqual(new MachineServerLinkView("Home", "waiting", null), Of("pending", "PendingApproval"));
        Assert.AreEqual(new MachineServerLinkView("Home", "waiting", null), Of("pending", null), "joined, before the node connects");
        Assert.AreEqual(new MachineServerLinkView("Home", "stopped", null), Of("approved", null));
        Assert.AreEqual(new MachineServerLinkView("Home", "revoked", null), Of("revoked", "Offline", "refused"));
        Assert.AreEqual(new MachineServerLinkView("https://home.example", "unknown", "no such device"), Of("approved", "Unauthenticated", "no such device", null),
            "a server whose name the node did not read yet is named by its address");
    }

    // The answers of a setup up to the pairing, then those given.
    private static SshNodeInstallerTests.ScriptedSsh Paired(params SshResult[] then)
    {
        var ssh = new SshNodeInstallerTests.ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Ubuntu.ReplaceLineEndings("\n"), ""));
        ssh.Answers.Enqueue(new SshResult(0, "4.2.0\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        ssh.Answers.Enqueue(new SshResult(0, Link + "\n", ""));
        foreach (var answer in then) ssh.Answers.Enqueue(answer);
        return ssh;
    }

    private static KeyValuePair<string, FleetDeviceListMember> Member(string id, string kind) => new(id, new FleetDeviceListMember(id, kind, null!, "alex"));
}
