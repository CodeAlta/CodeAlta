using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;

namespace CodeAlta.Tests;

/// <summary>
/// The Claude Code CLI CodeAlta pins: its archives, its download into the cache, and the order the executable is
/// looked for in.
/// </summary>
[TestClass]
public sealed class ClaudeCodePinnedCliTests
{
    private static readonly string Root = Path.GetPathRoot(Path.GetTempPath())!;
    private static readonly string Home = Path.Combine(Root, "home", "user");
    private static readonly string Cache = Path.Combine(Home, ".alta", "cache", "claude-code");

    [TestMethod]
    public void Pin_HasOneArchiveOfEachPlatformWithItsHash()
    {
        CollectionAssert.AreEquivalent(
            new[] { "win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64" },
            ClaudeCodePinnedCli.Assets.Select(static asset => asset.RuntimeIdentifier).ToArray());
        foreach (var asset in ClaudeCodePinnedCli.Assets)
        {
            Assert.AreEqual(64, asset.Sha256.Length, asset.FileName);
            Assert.IsTrue(asset.Sha256.All(char.IsAsciiHexDigitLower), asset.FileName);
            Assert.IsTrue(asset.Size > 10_000_000, asset.FileName);
            Assert.AreEqual(asset.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal), asset.IsZip, asset.FileName);
        }

        var windows = ClaudeCodePinnedCli.FindAsset("win-x64")!;
        Assert.AreEqual("claude.exe", windows.ExecutableName);
        Assert.AreEqual("claude", ClaudeCodePinnedCli.FindAsset("linux-musl-arm64")!.ExecutableName);
        Assert.AreEqual("claude-linux-arm64-musl.tar.gz", ClaudeCodePinnedCli.FindAsset("linux-musl-arm64")!.FileName);
        Assert.AreEqual($"https://github.com/anthropics/claude-code/releases/download/v{ClaudeCodePinnedCli.Version}/claude-win32-x64.zip",
            ClaudeCodePinnedCli.AddressOf(windows).AbsoluteUri);
        Assert.IsNull(ClaudeCodePinnedCli.FindAsset("freebsd-x64"));
        Assert.IsNull(ClaudeCodePinnedCli.FindAsset(null));
    }

    [TestMethod]
    public void Pin_KnowsThePlatformOfThisComputer()
    {
        var current = ClaudeCodePinnedCli.CurrentRuntimeIdentifier();

        Assert.IsNotNull(current);
        Assert.IsNotNull(ClaudeCodePinnedCli.FindAsset(current), current);
    }

    [TestMethod]
    public void Locator_RunsThePinnedVersionBeforeTheOneOfTheUser()
    {
        var installed = Path.Combine(Home, ".local", "bin", "claude");
        var pinned = Pinned("linux-x64");
        var marker = Path.Combine(Path.GetDirectoryName(pinned)!, ClaudeCodePinnedCli.MarkerFileName);

        var withPin = ClaudeCodeCliLocator.Resolve(null, Machine(installed, pinned, marker));
        var withoutMarker = ClaudeCodeCliLocator.Resolve(null, Machine(installed, pinned));
        var withoutPin = ClaudeCodeCliLocator.Resolve(null, Machine(installed));
        var configured = ClaudeCodeCliLocator.Resolve(installed, Machine(installed, pinned, marker));

        Assert.AreEqual((pinned, ClaudeCodeCliSource.Pinned), (withPin.Path, withPin.Source));
        Assert.AreEqual((installed, ClaudeCodeCliSource.Installed), (withoutMarker.Path, withoutMarker.Source), "A folder without its marker is not an installation.");
        Assert.AreEqual((installed, ClaudeCodeCliSource.Installed), (withoutPin.Path, withoutPin.Source));
        Assert.AreEqual((installed, ClaudeCodeCliSource.Configured), (configured.Path, configured.Source), "The command of the provider comes first.");
    }

    [TestMethod]
    public void Locator_SaysTheDownloadIsOfferedWhenNothingIsFound()
    {
        var resolution = ClaudeCodeCliLocator.Resolve(null, Machine());

        Assert.IsFalse(resolution.Found);
        StringAssert.Contains(resolution.Error, "Settings > Providers");
    }

    [TestMethod]
    [DataRow("2.1.292 (Claude Code)", "2.1.292")]
    [DataRow("claude 2.1.300-beta.1\n", "2.1.300-beta.1")]
    [DataRow("no version here", null)]
    [DataRow(null, null)]
    public void Version_IsReadFromWhatTheCliWrites(string? output, string? expected)
        => Assert.AreEqual(expected, ClaudeCodeCliVersion.Parse(output));

    [TestMethod]
    public void Version_OfAnotherCliIsSaid()
    {
        Assert.IsNull(ClaudeCodeCliVersion.DescribeDifference(ClaudeCodePinnedCli.Version));
        Assert.IsNull(ClaudeCodeCliVersion.DescribeDifference(null));
        StringAssert.Contains(ClaudeCodeCliVersion.DescribeDifference("9.9.9"), ClaudeCodePinnedCli.Version);
    }

    [TestMethod]
    public async Task Probe_OfTheCliOfTheUserSaysWhenItIsNotThePinnedVersion()
    {
        var cli = new ClaudeCodeFakeCli();
        var read = 0;
        await using var runtime = new ClaudeCodeModelProviderRuntime(new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude-code",
            TransportFactory = cli,
            GetEnvironmentVariable = static _ => null,
            ResolveCli = static () => new ClaudeCodeCliResolution("/fake/bin/claude", null) { Source = ClaudeCodeCliSource.Installed },
            ReadVersion = (_, _) => { read++; return Task.FromResult<string?>("9.9.9"); },
        });
        await using var pinnedRuntime = new ClaudeCodeModelProviderRuntime(new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude-code",
            TransportFactory = cli,
            GetEnvironmentVariable = static _ => null,
            ResolveCli = static () => new ClaudeCodeCliResolution("/cache/claude", null) { Source = ClaudeCodeCliSource.Pinned },
            ReadVersion = static (_, _) => throw new AssertFailedException("The pinned version is not asked."),
        });

        var probe = await runtime.ProbeAsync();
        var pinned = await pinnedRuntime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability);
        StringAssert.Contains(probe.StatusMessage, "9.9.9");
        StringAssert.Contains(probe.StatusMessage, ClaudeCodePinnedCli.Version);
        Assert.AreEqual(1, read);
        Assert.AreEqual(ModelProviderAvailability.Ready, pinned.Availability);
        Assert.IsFalse(pinned.StatusMessage!.Contains("9.9.9", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Installer_DownloadsChecksAndExtractsTheZipOfWindows()
    {
        using var fixture = new Fixture();
        var executable = Encoding.UTF8.GetBytes("MZ fake claude");
        var (asset, installer) = fixture.Create("win-x64", Zip(("claude.exe", executable)));
        var states = new List<string>();
        using var subscription = installer.Subscribe(snapshot => { lock (states) states.Add(snapshot.State); });

        Assert.IsNull(installer.InstalledPath);
        Assert.IsTrue(installer.TryStart());
        await installer.WaitAsync();

        Assert.AreEqual("done", installer.Current.State, installer.Current.Error);
        Assert.AreEqual(asset.Size, installer.Current.ReceivedBytes);
        var installed = installer.InstalledPath!;
        Assert.AreEqual(Path.Combine(fixture.Root, ClaudeCodePinnedCli.Version, "win-x64", "claude.exe"), installed);
        CollectionAssert.AreEqual(executable, File.ReadAllBytes(installed));
        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(installed)!, ClaudeCodePinnedCli.MarkerFileName)));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(installed)!, asset.FileName)), "The archive is not kept.");
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(installed) + ".partial"));
        lock (states)
        {
            CollectionAssert.IsSubsetOf(new[] { "idle", "downloading", "installing", "done" }, states);
        }

        // A second start finds it installed and downloads nothing.
        fixture.Http.Requests.Clear();
        Assert.IsTrue(installer.TryStart());
        await installer.WaitAsync();
        Assert.AreEqual("done", installer.Current.State);
        Assert.AreEqual(0, fixture.Http.Requests.Count);
        await installer.CloseAsync();
    }

    [TestMethod]
    public async Task Installer_TakesTheExecutableOutOfATarOfLinuxOrMacOS()
    {
        using var fixture = new Fixture();
        var executable = Encoding.UTF8.GetBytes("#!fake claude");
        var (_, installer) = fixture.Create("linux-x64", TarGz(("README", "readme"u8.ToArray()), ("package/claude", executable)));

        Assert.IsTrue(installer.TryStart());
        await installer.WaitAsync();

        Assert.AreEqual("done", installer.Current.State, installer.Current.Error);
        CollectionAssert.AreEqual(executable, File.ReadAllBytes(installer.InstalledPath!));
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsTrue(File.GetUnixFileMode(installer.InstalledPath!).HasFlag(UnixFileMode.UserExecute));
        }

        await installer.CloseAsync();
    }

    [TestMethod]
    public async Task Installer_ResumesWhereTheDownloadStopped()
    {
        using var fixture = new Fixture();
        var archive = Zip(("claude.exe", RandomNumberGenerator.GetBytes(200_000)));
        var (asset, installer) = fixture.Create("win-x64", archive);
        var partial = ClaudeCodePinnedCli.GetInstallDirectory(fixture.Root, "win-x64") + ".partial";
        Directory.CreateDirectory(partial);
        File.WriteAllBytes(Path.Combine(partial, asset.FileName + ".part"), archive[..50_000]);

        Assert.IsTrue(installer.TryStart());
        await installer.WaitAsync();

        Assert.AreEqual("done", installer.Current.State, installer.Current.Error);
        Assert.AreEqual(50_000L, fixture.Http.Requests.Single().RangeFrom);
        await installer.CloseAsync();
    }

    [TestMethod]
    public async Task Installer_RefusesAnArchiveWhoseHashIsNotThePinnedOne()
    {
        using var fixture = new Fixture();
        var archive = Zip(("claude.exe", "genuine"u8.ToArray()));
        var (asset, installer) = fixture.Create("win-x64", archive, sha256: new string('0', 64));

        Assert.IsTrue(installer.TryStart());
        await installer.WaitAsync();

        Assert.AreEqual(("failed", "checksum"), (installer.Current.State, installer.Current.Error));
        Assert.IsNull(installer.InstalledPath);
        var partial = ClaudeCodePinnedCli.GetInstallDirectory(fixture.Root, "win-x64") + ".partial";
        Assert.IsFalse(File.Exists(Path.Combine(partial, asset.FileName + ".part")), "What does not match is not resumed.");
        await installer.CloseAsync();
    }

    [TestMethod]
    public async Task Installer_SaysWhyItDidNotInstall()
    {
        using var fixture = new Fixture();
        var (_, missing) = fixture.Create("win-x64", Zip(("claude.exe", "x"u8.ToArray())));
        fixture.Http.Content.Clear();
        Assert.IsTrue(missing.TryStart());
        await missing.WaitAsync();
        Assert.AreEqual(("failed", "not_found"), (missing.Current.State, missing.Current.Error));
        await missing.CloseAsync();

        var unsupported = new ClaudeCodeCliInstaller(fixture.Root, fixture.Http, runtimeIdentifier: "freebsd-x64");
        Assert.IsFalse(unsupported.IsSupported);
        Assert.IsTrue(unsupported.TryStart());
        await unsupported.WaitAsync();
        Assert.AreEqual(("failed", "unsupported_platform"), (unsupported.Current.State, unsupported.Current.Error));
        await unsupported.CloseAsync();
    }

    [TestMethod]
    public async Task Installer_LeavesTheDownloadToTheInstanceThatHoldsTheLock()
    {
        using var fixture = new Fixture();
        var (_, installer) = fixture.Create("win-x64", Zip(("claude.exe", "x"u8.ToArray())));
        var partial = ClaudeCodePinnedCli.GetInstallDirectory(fixture.Root, "win-x64") + ".partial";
        Directory.CreateDirectory(partial);

        // The developer and the normal instance share the cache: the one that holds the lock downloads.
        using (new FileStream(Path.Combine(partial, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsTrue(installer.TryStart());
            await installer.WaitAsync();
            Assert.AreEqual(("failed", "busy_elsewhere"), (installer.Current.State, installer.Current.Error));
        }

        Assert.IsTrue(installer.TryStart());
        await installer.WaitAsync();
        Assert.AreEqual("done", installer.Current.State);
        await installer.CloseAsync();
    }

    [TestMethod]
    public async Task Installer_CanBeCanceledAndStartsOneDownloadAtATime()
    {
        using var fixture = new Fixture();
        var (asset, installer) = fixture.Create("win-x64", Zip(("claude.exe", "x"u8.ToArray())));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Hold = held.Task;

        Assert.IsTrue(installer.TryStart());
        Assert.IsFalse(installer.TryStart(), "One download at a time.");
        installer.Cancel();
        await installer.WaitAsync();

        Assert.AreEqual("canceled", installer.Current.State);
        Assert.IsNull(installer.InstalledPath);
        held.SetResult();
        await installer.CloseAsync();
        Assert.IsFalse(installer.TryStart(), "A closed installer starts nothing.");
        _ = asset;
    }

    private static string Pinned(string runtimeIdentifier)
        => Path.Combine(ClaudeCodePinnedCli.GetInstallDirectory(Cache, runtimeIdentifier), ClaudeCodePinnedCli.FindAsset(runtimeIdentifier)!.ExecutableName);

    private static ClaudeCodeCliEnvironment Machine(params string[] files)
    {
        var existing = new HashSet<string>(files.Select(Path.GetFullPath), StringComparer.Ordinal);
        return new ClaudeCodeCliEnvironment(false, null, Home, null,
            path => existing.Contains(Path.GetFullPath(path)))
        {
            PinnedRoot = Cache,
            RuntimeIdentifier = "linux-x64",
        };
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] TarGz(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
            }
        }

        return stream.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-claude-cli-" + Guid.NewGuid().ToString("N"));

        public Handler Http { get; } = new();

        public (ClaudeCodeCliAsset Asset, ClaudeCodeCliInstaller Installer) Create(string runtimeIdentifier, byte[] archive, string? sha256 = null)
        {
            var pinned = ClaudeCodePinnedCli.FindAsset(runtimeIdentifier)!;
            var asset = pinned with { Size = archive.Length, Sha256 = sha256 ?? Convert.ToHexStringLower(SHA256.HashData(archive)) };
            Http.Content[ClaudeCodePinnedCli.AddressOf(asset).AbsoluteUri] = archive;
            return (asset, new ClaudeCodeCliInstaller(Root, Http, asset));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A file still held by a failed test stays in the temporary folder.
            }
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Content { get; } = [];

        public List<(string Url, long? RangeFrom)> Requests { get; } = [];

        public Task? Hold { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var from = request.Headers.Range?.Ranges.Single().From;
            lock (Requests)
            {
                Requests.Add((url, from));
            }

            if (Hold is { } hold)
            {
                await hold.WaitAsync(cancellationToken);
            }

            if (!Content.TryGetValue(url, out var bytes))
            {
                return new(HttpStatusCode.NotFound);
            }

            var body = bytes.AsSpan((int)(from ?? 0)).ToArray();
            var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentLength = body.Length;
            if (from is not null)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from.Value, bytes.Length - 1, bytes.Length);
            }

            return response;
        }
    }
}
