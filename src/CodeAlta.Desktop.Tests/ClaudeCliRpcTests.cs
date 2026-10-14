using CodeAlta.Agent.Claude;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Hosting;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ClaudeCliRpcTests
{
    [TestMethod]
    public async Task UnownedAndStaleRequests_HaveNoAccess()
    {
        var unavailable = new ClaudeCliService();
        Assert.AreEqual("unavailable", (await unavailable.ReadAsync(new(null), default)).Status);
        Assert.AreEqual("unavailable", unavailable.Install(new(null)).Status);
        Assert.AreEqual("unavailable", unavailable.CancelInstall(new(null)).Status);
        Assert.AreEqual(0, await CountAsync(unavailable.WatchAsync(new(null), default)));
        await unavailable.CloseAsync();

        using var fixture = new Fixture("win-x64");
        Assert.AreEqual("stale_epoch", (await fixture.Service.ReadAsync(new("old"), default)).Status);
        Assert.AreEqual("stale_epoch", fixture.Service.Install(new("old")).Status);
        Assert.AreEqual(0, await CountAsync(fixture.Service.WatchAsync(new("old"), default)));
        Assert.IsFalse(Directory.Exists(fixture.Root), "Nothing is written by a request that is refused.");
        await fixture.Service.CloseAsync();
    }

    [TestMethod]
    public async Task Read_SaysWhatIsInTheCacheAndWhatTheUserInstalled()
    {
        using var fixture = new Fixture("win-x64") { Installed = @"C:\Users\me\.local\bin\claude.exe", Version = "2.1.300" };

        var before = await fixture.Service.ReadAsync(new("host"), default);
        Assert.AreEqual(("ok", true, ClaudeCodePinnedCli.Version, ClaudeCodePinnedCli.FindAsset("win-x64")!.Size, null),
            (before.Status, before.Supported, before.Version, before.Size, before.PinnedPath));
        Assert.AreEqual((fixture.Installed, "2.1.300"), (before.InstalledPath, before.InstalledVersion));
        Assert.AreEqual(OperatingSystem.IsWindows(), before.Windows);

        // What the download leaves: the executable and the marker of a complete installation.
        var folder = ClaudeCodePinnedCli.GetInstallDirectory(fixture.Root, "win-x64");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "claude.exe"), "");
        File.WriteAllText(Path.Combine(folder, ClaudeCodePinnedCli.MarkerFileName), "{}");
        var after = await fixture.Service.ReadAsync(new("host"), default);

        Assert.AreEqual(Path.Combine(folder, "claude.exe"), after.PinnedPath);
        Assert.AreEqual(1, fixture.VersionReads, "The version of an executable that did not change is read once.");
        await fixture.Service.CloseAsync();
    }

    [TestMethod]
    public async Task Check_SaysWhichExecutableAProviderWouldRun()
    {
        using var fixture = new Fixture("win-x64");
        var missing = Path.Combine(fixture.Root, "nowhere", "claude.exe");
        var existing = Path.Combine(fixture.Root, "tools", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "");
        var service = new ClaudeCliService(fixture.Service.Installer!, "host", static () => null, static (_, _) => Task.FromResult<string?>(null))
        {
            FindProvider = key => key switch
            {
                "claude-missing" => new ClaudeCliProvider("claude-code", missing, "~/.claude-work"),
                "claude-tools" => new ClaudeCliProvider("claude-code", existing, null),
                "codex" => new ClaudeCliProvider("codex", null, null),
                "broken" => throw new InvalidDataException("The configuration cannot be read."),
                _ => null,
            },
        };

        var none = await service.CheckAsync(new("host", "claude-missing"), default);
        Assert.AreEqual(("ok", true, null, "~/.claude-work", true), (none.Status, none.Claude, none.Executable, none.ConfigDir, none.Supported));
        Assert.AreEqual(ClaudeCodePinnedCli.Version, none.Version);
        Assert.AreEqual(existing, (await service.CheckAsync(new("host", "claude-tools"), default)).Executable);
        var other = await service.CheckAsync(new("host", "codex"), default);
        Assert.AreEqual(("ok", false, null), (other.Status, other.Claude, other.Executable));
        Assert.AreEqual("unknown_provider", (await service.CheckAsync(new("host", "missing"), default)).Status);
        Assert.AreEqual("read_failed", (await service.CheckAsync(new("host", "broken"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.CheckAsync(new("host", " "), default)).Status);
        Assert.AreEqual("stale_epoch", (await service.CheckAsync(new("old", "claude-missing"), default)).Status);
        Assert.AreEqual("unavailable", (await new ClaudeCliService().CheckAsync(new(null, "claude-missing"), default)).Status);
        await fixture.Service.CloseAsync();
    }

    [TestMethod]
    public async Task Install_OfAPlatformWithoutCli_IsRefused()
    {
        using var fixture = new Fixture("freebsd-x64");

        Assert.IsFalse((await fixture.Service.ReadAsync(new("host"), default)).Supported);
        Assert.AreEqual("unsupported", fixture.Service.Install(new("host")).Status);
        await fixture.Service.CloseAsync();
    }

    private static async Task<int> CountAsync<T>(IAsyncEnumerable<T> items)
    {
        var count = 0;
        await foreach (var _ in items) count++;
        return count;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string runtimeIdentifier)
        {
            Service = new ClaudeCliService(new ConfiguredClaudeCodeCli(Root, new HttpClientHandler(), runtimeIdentifier), "host", () => Installed,
                (_, _) => { VersionReads++; return Task.FromResult(Version); });
        }

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-claude-cli-rpc-" + Guid.NewGuid().ToString("N"));

        public ClaudeCliService Service { get; }

        public string? Installed { get; init; }

        public string? Version { get; init; }

        public int VersionReads { get; private set; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
