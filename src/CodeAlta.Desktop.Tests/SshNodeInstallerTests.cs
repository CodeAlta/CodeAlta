using CodeAlta.Desktop.Fleet.MachineSetup;

namespace CodeAlta.Desktop.Tests;

/// <summary>The setup of a machine over SSH (<see cref="SshNodeInstaller"/>) against a scripted ssh: its steps, the commands it runs and its failures.</summary>
[TestClass]
public sealed class SshNodeInstallerTests
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
        linger=no
        """;

    private const string MacWithDesktop = """
        os=Darwin
        arch=arm64
        home=/Users/me
        user=me
        hostname=mac
        dotnet=/usr/local/share/dotnet/dotnet
        sdk=10.0.401 [/usr/local/share/dotnet/sdk]
        sdk=10.0.402 [/usr/local/share/dotnet/sdk]
        sdk=9.0.100 [/usr/local/share/dotnet/sdk]
        runtime=Microsoft.NETCore.App 10.0.13 [/usr/local/share/dotnet/shared/Microsoft.NETCore.App]
        desktop=running
        """;

    private const string Link = "codealta-pair://pair?v=1&id=abc&name=pc-ai&fp=x&addr=10.0.0.2%3A2585&secret=s&exp=1";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Targets_AreReadAsSshTakesThem_AndNeverAsOptions()
    {
        Assert.IsTrue(SshTarget.TryParse("xoofx@pc-ai.home.arpa", out var target));
        Assert.AreEqual(new SshTarget("xoofx", "pc-ai.home.arpa", null), target);
        Assert.IsTrue(SshTarget.TryParse("me@box:2222", out target));
        Assert.AreEqual(2222, target.Port);
        Assert.IsTrue(SshTarget.TryParse("me@[fd00::1]:22", out target));
        Assert.AreEqual("fd00::1", target.Host);
        Assert.IsTrue(SshTarget.TryParse("build-box", out target));
        Assert.AreEqual("build-box", target.Destination);
        Assert.IsFalse(SshTarget.TryParse("-oProxyCommand=evil", out _));
        Assert.IsFalse(SshTarget.TryParse("me@box;reboot", out _));
        Assert.IsFalse(SshTarget.TryParse("me@box:99999", out _));
    }

    [TestMethod]
    public void Probe_FindsTheSystemAndTheNewestDotNet10()
    {
        var mac = SshNodeInstaller.ReadFacts(MacWithDesktop.ReplaceLineEndings("\n"));
        Assert.AreEqual("osx-arm64", mac.RuntimeIdentifier);
        Assert.AreEqual("10.0.402", mac.Sdk, "the newest SDK of .NET 10 or later");
        Assert.AreEqual("10.0.13", mac.Runtime);
        Assert.IsTrue(mac.DesktopRuns);
        var linux = SshNodeInstaller.ReadFacts(Ubuntu.ReplaceLineEndings("\n"));
        Assert.AreEqual("linux-x64", linux.RuntimeIdentifier);
        Assert.AreEqual("no", linux.Linger);
        var musl = SshNodeInstaller.ReadFacts("os=Linux\narch=aarch64\nlibc=musl\n");
        Assert.AreEqual("linux-musl-arm64", musl.RuntimeIdentifier);
        Assert.IsNull(SshNodeInstaller.ReadFacts("os=FreeBSD\narch=amd64\n").RuntimeIdentifier);
        Assert.IsNull(SshNodeInstaller.ReadFacts("os=Linux\narch=x86_64\ndotnet=/usr/bin/dotnet\nsdk=8.0.100 [x]\n").Sdk, ".NET 8 is too old");
    }

    [TestMethod]
    public async Task Setup_OnLinux_InstallsTheToolAndItsService_ThenPairsWithTheLinkTheNodeMakes()
    {
        var ssh = new ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Ubuntu.ReplaceLineEndings("\n"), ""));
        ssh.Answers.Enqueue(new SshResult(0, "3.4.1\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, "Lingering is now on for me.\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, "Give this pairing link...\n" + Link + "\n", "") { });
        var steps = new List<SshSetupProgress>();
        string? redeemed = null;

        var machine = await new SshNodeInstaller(ssh, "3.4.1+abcdef").RunAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "pc-ai", null), Port = 2690 },
            (link, _) => { redeemed = link; return Task.FromResult("machine-1"); }, steps.Add, TestContext.CancellationToken);

        Assert.AreEqual("machine-1", machine);
        Assert.AreEqual(Link, redeemed, "the desktop redeems the link the node printed");
        CollectionAssert.AreEqual(new[] { "connect:running", "connect:done", "check:running", "check:done", "install:running", "install:done", "service:running",
            "service:done", "pair:running" }, steps.Select(static step => $"{step.Step}:{step.State}").ToArray());
        Assert.AreEqual("Linux x86_64 · .NET SDK 10.0.114", steps.Single(static step => step is { Step: "check", State: "done" }).Detail);
        StringAssert.Contains(ssh.Scripts[1], "tool update --tool-path \"$TOOLS\" CodeAlta.Node --version '3.4.1' --allow-downgrade", "the version of this desktop, without its build metadata");
        StringAssert.Contains(ssh.Scripts[1], "TOOLS=\"$HOME\"/'.dotnet/tools'");
        StringAssert.Contains(ssh.Scripts[2], "install --listen-all --port 2690");
        Assert.IsFalse(ssh.Scripts[2].Contains("--data-root", StringComparison.Ordinal), "~/.alta when no desktop runs there");
        StringAssert.Contains(ssh.Scripts[3], "pair --minutes 5", "the desktop manages the machine only when the person ticks it");
        Assert.IsTrue(ssh.Scripts.All(static script => !script.Contains('\r')), "sh reads no carriage return");
    }

    [TestMethod]
    public async Task Setup_WhereCodeAltaDesktopRuns_GivesTheNodeAProfileOfItsOwn()
    {
        var ssh = new ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, MacWithDesktop.ReplaceLineEndings("\n"), ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        ssh.Answers.Enqueue(new SshResult(0, Link + "\n", ""));
        var steps = new List<SshSetupProgress>();

        await new SshNodeInstaller(ssh, "4.0.0").RunAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "mac", null), ToolPath = "/tmp/t", Manage = true },
            (_, _) => Task.FromResult("mac-1"), steps.Add, TestContext.CancellationToken);

        StringAssert.Contains(steps.Single(static step => step is { Step: "check", State: "done" }).Detail, "~/.alta-node");
        StringAssert.Contains(ssh.Scripts[2], "ROOT=\"$HOME\"/'.alta-node'");
        StringAssert.Contains(ssh.Scripts[2], "--data-root \"$ROOT\"");
        StringAssert.Contains(ssh.Scripts[3], "pair --manage --minutes 5 --data-root \"$ROOT\"");
        StringAssert.Contains(ssh.Scripts[1], "TOOLS='/tmp/t'");
    }

    [TestMethod]
    public async Task Setup_SaysWhyItStops_SoThatThePageSaysWhatToDo()
    {
        Assert.AreEqual("host_key_unknown", await FailureAsync(new SshResult(255, "", "Host key verification failed.\n")));
        Assert.AreEqual("auth_failed", await FailureAsync(new SshResult(255, "", "me@box: Permission denied (publickey).\n")));
        Assert.AreEqual("unknown_host", await FailureAsync(new SshResult(255, "", "ssh: Could not resolve hostname box: No such host is known.\n")));
        Assert.AreEqual("unreachable", await FailureAsync(new SshResult(255, "", "ssh: connect to host box port 22: Connection timed out\n")));
        Assert.AreEqual("unsupported_system", await FailureAsync(new SshResult(0, "os=\narch=\n", "")));
        Assert.AreEqual("dotnet_missing", await FailureAsync(new SshResult(0, "os=Linux\narch=x86_64\ndotnet=\n", "")));
        Assert.AreEqual("tool_install_failed", await FailureAsync(new SshResult(0, Ubuntu.ReplaceLineEndings("\n"), ""), new SshResult(1, "", "error NU1101: Unable to find package")));
        // The pair step: the node never printed a link.
        Assert.AreEqual("pair_failed", await FailureAsync(new SshResult(0, Ubuntu.ReplaceLineEndings("\n"), ""), new SshResult(0, "", ""), new SshResult(0, "", ""),
            new SshResult(1, "", "altanode: no node runs on this profile.")));
    }

    // Always on: the setup reads after install what altanode status says, instead of assuming lingering is on.
    [TestMethod]
    public async Task Setup_OnLinux_SaysWhatHappensAfterARestart_AsTheNodeReadsIt()
    {
        var ssh = new ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, Ubuntu.ReplaceLineEndings("\n"), ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        ssh.Answers.Enqueue(new SshResult(0, "Lingering is off for me and loginctl enable-linger me needs privileges here.\n--- altanode status\nService: a systemd --user unit of me.\n"
            + "After a restart: it waits until me logs in (lingering is off: sudo loginctl enable-linger me starts it at boot).\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, Link + "\n", ""));
        var steps = new List<SshSetupProgress>();
        await new SshNodeInstaller(ssh, "4.1.0").RunAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "pc-ai", null) },
            (_, _) => Task.FromResult("m"), steps.Add, TestContext.CancellationToken);

        Assert.AreEqual("systemd, port 2585 · After a restart: it waits until me logs in (lingering is off: sudo loginctl enable-linger me starts it at boot).",
            steps.Single(static step => step is { Step: "service", State: "done" }).Detail);
        Assert.IsFalse(ssh.Scripts[2].Contains("sudo", StringComparison.Ordinal), "without Always on, nothing runs as root");
        StringAssert.Contains(ssh.Scripts[2], "echo '--- altanode status'\n\"$TOOLS/altanode\" status 2>/dev/null || true\n");
    }

    [TestMethod]
    public async Task AlwaysOn_AsksTheSudoPasswordWhenNeeded_AndGivesItToSudoOnly()
    {
        var mac = MacWithDesktop.ReplaceLineEndings("\n") + "\nsudo=password\n";
        // The check stops for the password, before anything is installed.
        var ssh = new ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, mac, ""));
        var asked = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshNodeInstaller(ssh, "4.1.0").RunAsync(
            new SshNodeSetupRequest { Target = new SshTarget("me", "mac", null), AlwaysOn = true }, (_, _) => Task.FromResult("m"), static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual(("check", "sudo_password_needed"), (asked.Step, asked.Code));
        Assert.AreEqual(1, ssh.Scripts.Count);

        // With it: the LaunchAgent goes, and a LaunchDaemon runs the person's tool as them, the password on the standard input of sudo.
        ssh = new ScriptedSsh();
        ssh.Answers.Enqueue(new SshResult(0, mac, ""));
        ssh.Answers.Enqueue(new SshResult(0, "", ""));
        ssh.Answers.Enqueue(new SshResult(0, "--- altanode status\nAfter a restart: it starts at boot as me, without a login.\n", ""));
        ssh.Answers.Enqueue(new SshResult(0, Link + "\n", ""));
        var steps = new List<SshSetupProgress>();
        await new SshNodeInstaller(ssh, "4.1.0").RunAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "mac", null), AlwaysOn = true, SudoPassword = "it's s3cret" },
            (_, _) => Task.FromResult("m"), steps.Add, TestContext.CancellationToken);
        var service = ssh.Scripts[2];
        StringAssert.Contains(service, "\"$TOOLS/altanode\" uninstall --data-root \"$ROOT\" >/dev/null 2>&1 || true\n");
        StringAssert.Contains(service, "printf '%s\\n' 'it'\\''s s3cret' | sudo -S -p '' env \"DOTNET_ROOT=$DOTNET_ROOT\" \"PATH=$PATH\" \"$TOOLS/altanode\" install --system --account \"$ME\" --listen-all --port 2585 --data-root \"$ROOT\"\n");
        Assert.AreEqual(1, ssh.Scripts.Count(static script => script.Contains("s3cret", StringComparison.Ordinal)), "only the step that needs it has it");
        StringAssert.EndsWith(steps.Single(static step => step is { Step: "service", State: "done" }).Detail, "it starts at boot as me, without a login.");

        // On Linux, sudo turns lingering on when altanode install could not; without a password, sudo -n.
        ssh = Scripted(new SshResult(0, Ubuntu.ReplaceLineEndings("\n") + "\nsudo=nopassword\n", ""), new SshResult(0, "", ""), new SshResult(0, "", ""), new SshResult(0, Link + "\n", ""));
        await new SshNodeInstaller(ssh, "4.1.0").RunAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "pc-ai", null), AlwaysOn = true },
            (_, _) => Task.FromResult("m"), static _ => { }, TestContext.CancellationToken);
        StringAssert.Contains(ssh.Scripts[2], "\"$TOOLS/altanode\" install --listen-all --port 2585\n");
        StringAssert.Contains(ssh.Scripts[2], "!= yes ]; then sudo -n loginctl enable-linger \"$ME\"; fi\n");

        // A wrong password, or no sudo there: the page says so, with the command to run there.
        Assert.AreEqual("sudo_refused", await FailureAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "mac", null), AlwaysOn = true, SudoPassword = "wrong" },
            new SshResult(0, mac, ""), new SshResult(0, "", ""), new SshResult(1, "", "Sorry, try again.\nsudo: 1 incorrect password attempt\n")));
        var missing = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshNodeInstaller(Scripted(new SshResult(0, Ubuntu.ReplaceLineEndings("\n"), "")), "4.1.0").RunAsync(
            new SshNodeSetupRequest { Target = new SshTarget("me", "pc-ai", null), AlwaysOn = true }, (_, _) => Task.FromResult("m"), static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual(("sudo_missing", "sudo loginctl enable-linger me"), (missing.Code, missing.Message));
    }

    [TestMethod]
    public void Probe_ReadsHowACommandRunsAsRoot()
    {
        Assert.AreEqual("password", SshNodeInstaller.ReadFacts("os=Darwin\narch=arm64\nsudo=password\n").Sudo);
        Assert.AreEqual("root", SshNodeInstaller.ReadFacts("os=Linux\narch=x86_64\nsudo=root\n").Sudo);
        Assert.IsNull(SshNodeInstaller.ReadFacts("os=Linux\narch=x86_64\nsudo=maybe\n").Sudo);
        Assert.IsFalse(SshNodeInstaller.ReadFacts("os=Linux\narch=x86_64\nlinger=yes\n").AlwaysOnNeedsRoot, "lingering is on already");
        Assert.IsTrue(SshNodeInstaller.ReadFacts("os=Darwin\narch=arm64\n").AlwaysOnNeedsRoot);
    }

    [TestMethod]
    public void Words_AreQuotedForSh()
    {
        Assert.AreEqual("'a b'", SshNodeInstaller.Quote("a b"));
        Assert.AreEqual("'it'\\''s'", SshNodeInstaller.Quote("it's"));
        Assert.AreEqual("\"$HOME\"/'.alta node'", SshNodeInstaller.Quote("~/.alta node"));
        Assert.AreEqual("3.4.1", SshNodeInstaller.ReleaseVersion("3.4.1+abc"));
    }

    [TestMethod]
    public void LocalPackages_TakeTheNewestVersionThatHasBothPackages()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-ssh-packages-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (var name in new[] { "CodeAlta.Node.1.0.0.nupkg", "CodeAlta.Node.linux-x64.1.0.0.nupkg", "CodeAlta.Node.1.1.0.nupkg", "CodeAlta.Node.osx-arm64.1.1.0.nupkg" })
                File.WriteAllText(Path.Combine(folder, name), "");
            var linux = SshNodeInstaller.LocalPackages(folder, "linux-x64");
            Assert.AreEqual("1.0.0", linux.Version, "1.1.0 has no package of linux-x64");
            Assert.AreEqual("1.1.0", SshNodeInstaller.LocalPackages(folder, "osx-arm64").Version);
            Assert.AreEqual("packages_missing", Assert.ThrowsExactly<SshSetupException>(() => SshNodeInstaller.LocalPackages(folder, "linux-arm64")).Code);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnknownHostKey_SaysTheFingerprintOfTheKey_BeforeThePersonTrustsIt()
    {
        var ssh = new ScriptedSsh { HostKey = "SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)" };
        ssh.Answers.Enqueue(new SshResult(SshResult.ConnectionFailed, "", "Host key verification failed.\n"));
        var failure = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshNodeInstaller(ssh, "4.0.0").RunAsync(
            new SshNodeSetupRequest { Target = new SshTarget("me", "box", null) }, (_, _) => Task.FromResult("m"), static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual("host_key_unknown", failure.Code);
        Assert.AreEqual("SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)", failure.Message, "the key the person compares before Trust it");
        Assert.AreEqual(1, ssh.HostKeyReads);

        // The same for a server set up over SSH.
        ssh.Answers.Enqueue(new SshResult(SshResult.ConnectionFailed, "", "Host key verification failed.\n"));
        var server = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshServerInstaller(ssh, "4.0.0").RunAsync(
            new SshServerSetupRequest { Target = new SshTarget("me", "box", null) }, (_, _, _) => Task.FromResult<ReachProblem?>(null),
            (_, _, _, _, _) => Task.FromResult("link"), static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual("SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)", server.Message);
    }

    [TestMethod]
    public void HostKeyFingerprint_IsTheHashOfTheKey_AsSshKeygenPrintsIt()
    {
        var key = Convert.ToBase64String([.. Enumerable.Range(0, 51).Select(static i => (byte)i)]);
        var hash = "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(key))).TrimEnd('=');
        Assert.AreEqual((hash, hash + " (ED25519)"), OpenSshRunner.FingerprintOf($"|1|c2FsdA==|aGFzaA== ssh-ed25519 {key}\n"));
        Assert.AreEqual(hash + " (RSA)", OpenSshRunner.FingerprintOf($"# a comment\nbox ssh-rsa {key}\nbox ssh-ed25519 {key}\n")?.Text, "the first key ssh recorded");
        Assert.AreEqual(hash + " (ECDSA)", OpenSshRunner.FingerprintOf($"box ecdsa-sha2-nistp256 {key}")?.Text);
        Assert.IsNull(OpenSshRunner.FingerprintOf(""));
        Assert.IsNull(OpenSshRunner.FingerprintOf("not a key\n"));
    }

    private Task<string> FailureAsync(params SshResult[] answers) => FailureAsync(new SshNodeSetupRequest { Target = new SshTarget("me", "box", null) }, answers);

    private async Task<string> FailureAsync(SshNodeSetupRequest request, params SshResult[] answers)
    {
        var failure = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new SshNodeInstaller(Scripted(answers), "4.0.0").RunAsync(
            request, (_, _) => Task.FromResult("m"), static _ => { }, TestContext.CancellationToken));
        return failure.Code;
    }

    private static ScriptedSsh Scripted(params SshResult[] answers)
    {
        var ssh = new ScriptedSsh();
        foreach (var answer in answers) ssh.Answers.Enqueue(answer);
        return ssh;
    }

    /// <summary>An ssh that answers each script in turn, and prints the lines of its output as they come.</summary>
    internal sealed class ScriptedSsh : ISshRunner
    {
        public Queue<SshResult> Answers { get; } = new();

        public List<string> Scripts { get; } = [];

        public Task<SshResult> RunAsync(SshTarget target, string script, string? trustedHostKey, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Scripts.Add(script);
            var answer = Answers.Count > 0 ? Answers.Dequeue() : new SshResult(0, "", "");
            foreach (var line in answer.Output.Split('\n')) onOutput?.Invoke(line);
            return Task.FromResult(answer);
        }

        public Task<SshResult> CopyAsync(SshTarget target, IReadOnlyList<string> files, string folder, string? trustedHostKey, CancellationToken cancellationToken)
            => Task.FromResult(new SshResult(0, "", ""));

        public string? HostKey { get; init; }

        public int HostKeyReads { get; private set; }

        public Task<string?> ReadHostKeyAsync(SshTarget target, CancellationToken cancellationToken)
        {
            HostKeyReads++;
            return Task.FromResult(HostKey);
        }

        public int Forwards { get; private set; }

        public Task<SshForward> ForwardAsync(SshTarget target, int remotePort, string? trustedHostKey, CancellationToken cancellationToken)
        {
            Forwards++;
            return Task.FromResult(new SshForward(40000 + remotePort, static () => { }));
        }
    }
}
