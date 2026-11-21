using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Acp;
using CodeAlta.Plugin.Acp;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

/// <summary>
/// What the catalog of the ACP registry installs: the archive of the registry, or for Google Antigravity the CLI of
/// its vendor (<c>agy acp</c>), read from its release manifest and checked against its SHA-512.
/// </summary>
[TestClass]
public sealed class AcpRegistryCatalogTests
{
    private const string RegistryUrl = "https://registry.test/registry.json";
    private const string RegistryArchiveUrl = "https://registry.test/antigravity/agy_acp_server.bin";
    private const string ManifestRoot = "https://antigravity-cli-auto-updater-974169037036.us-central1.run.app/manifests/";

    private static readonly byte[] CliBytes = Encoding.UTF8.GetBytes("the Antigravity CLI");
    private static readonly byte[] ServerBytes = Encoding.UTF8.GetBytes("the server of the registry");

    private string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "codealta-acp-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AddingAntigravity_InstallsTheCliOfItsVendor_AndRunsItsAcpServer()
    {
        using var handler = new Handler();
        handler.AddRegistry();
        handler.AddCli("1.5.14", CliBytes);
        using var client = new HttpClient(handler);
        var catalog = new AcpRegistryCatalog(_root, client, new Uri(RegistryUrl));

        var listed = await catalog.ListAsync(refresh: false, CancellationToken.None);
        var result = await catalog.InstallAsync("antigravity-acp", new Progress(), CancellationToken.None);

        Assert.AreEqual("1.5.14", listed.Entries.Single(static entry => entry.Id == "antigravity-acp").Version, "the version that is installed, not the one of the registry");
        Assert.AreEqual("1.2.0", listed.Entries.Single(static entry => entry.Id == "other-agent").Version, "the other agents are the ones of the registry");
        var command = (string)result.Values[AcpAgentSettings.CommandKey]!;
        Assert.AreEqual(OperatingSystem.IsWindows() ? "agy.exe" : "antigravity", Path.GetFileName(command));
        CollectionAssert.AreEqual(CliBytes, await File.ReadAllBytesAsync(command));
        CollectionAssert.AreEqual(new object?[] { "acp" }, (object?[])result.Values[AcpAgentSettings.ArgumentsKey]!);
        Assert.AreEqual("antigravity-acp", result.Values[AcpAgentSettings.AgentKey]);
        Assert.AreEqual("1.5.14", result.Values[AcpAgentSettings.AgentVersionKey]);
        Assert.IsFalse(handler.Requested.Contains(RegistryArchiveUrl), "the server of the registry is not downloaded");

        var agent = new AcpAgentSettings(command, ["acp"], new Dictionary<string, string>(), "antigravity-acp", "1.5.14");
        Assert.IsInstanceOfType<AntigravityCliQuirks>(AcpAgentQuirks.For(agent));
    }

    [TestMethod]
    public async Task ADownloadThatIsNotTheRelease_IsNotInstalled()
    {
        using var handler = new Handler();
        handler.AddRegistry();
        handler.AddCli("1.5.14", CliBytes, sha512: new string('0', 128));
        using var client = new HttpClient(handler);
        var catalog = new AcpRegistryCatalog(_root, client, new Uri(RegistryUrl));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => catalog.InstallAsync("antigravity-acp", new Progress(), CancellationToken.None));

        StringAssert.Contains(error.Message, "SHA-512");
        Assert.IsFalse(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any(static file => Path.GetFileName(file) is "agy.exe" or "antigravity"));
    }

    [TestMethod]
    public async Task WithoutTheReleaseOfItsVendor_AntigravityIsAddedFromTheRegistry()
    {
        using var handler = new Handler();
        handler.AddRegistry();
        using var client = new HttpClient(handler);
        var catalog = new AcpRegistryCatalog(_root, client, new Uri(RegistryUrl));

        var result = await catalog.InstallAsync("antigravity-acp", new Progress(), CancellationToken.None);

        var command = (string)result.Values[AcpAgentSettings.CommandKey]!;
        Assert.AreEqual("agy_acp_server", Path.GetFileNameWithoutExtension(command));
        Assert.AreEqual("1.5.0", result.Values[AcpAgentSettings.AgentVersionKey]);
        Assert.IsInstanceOfType<AntigravityAcpQuirks>(
            AcpAgentQuirks.For(new AcpAgentSettings(command, [], new Dictionary<string, string>(), "antigravity-acp", "1.5.0")));
    }

    [TestMethod]
    public async Task AProviderThatRunsTheServerOfTheRegistry_IsUpdatedToTheCli()
    {
        using var handler = new Handler();
        handler.AddRegistry();
        handler.AddCli("1.5.14", CliBytes);
        using var client = new HttpClient(handler);
        var catalog = new AcpRegistryCatalog(_root, client, new Uri(RegistryUrl));
        var settings = new PluginProviderSettings("antigravity-acp", AcpAgentSettings.ProviderType, new Dictionary<string, object?>
        {
            [AcpAgentSettings.AgentKey] = "antigravity-acp",
            [AcpAgentSettings.AgentVersionKey] = "1.5.0",
            [AcpAgentSettings.CommandKey] = Path.Combine(_root, "agy_acp_server.exe"),
        });

        var update = await catalog.GetUpdateAsync(settings, CancellationToken.None);
        var values = await catalog.UpdateAsync(settings, new Progress(), CancellationToken.None);

        Assert.AreEqual("1.5.14", update);
        Assert.AreEqual("1.5.14", values[AcpAgentSettings.AgentVersionKey]);
        Assert.AreEqual(OperatingSystem.IsWindows() ? "agy.exe" : "antigravity", Path.GetFileName((string)values[AcpAgentSettings.CommandKey]!));
        CollectionAssert.AreEqual(new object?[] { "acp" }, (object?[])values[AcpAgentSettings.ArgumentsKey]!);
    }

    [TestMethod]
    public async Task TheCli_IsNotUpdatedToTheServerOfTheRegistry_WhenItsReleaseCannotBeRead()
    {
        using var handler = new Handler();
        handler.AddRegistry(antigravityVersion: "9.0.0");
        using var client = new HttpClient(handler);
        var catalog = new AcpRegistryCatalog(_root, client, new Uri(RegistryUrl));
        var settings = new PluginProviderSettings("antigravity-acp", AcpAgentSettings.ProviderType, new Dictionary<string, object?>
        {
            [AcpAgentSettings.AgentKey] = "antigravity-acp",
            [AcpAgentSettings.AgentVersionKey] = "1.5.14",
            [AcpAgentSettings.CommandKey] = Path.Combine(_root, "agy.exe"),
        });

        Assert.IsNull(await catalog.GetUpdateAsync(settings, CancellationToken.None), "the registry has another program, with versions of its own");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.UpdateAsync(settings, new Progress(), CancellationToken.None));
        Assert.IsFalse(handler.Requested.Contains(RegistryArchiveUrl));
    }

    private sealed class Progress : IProgress<PluginProviderInstallProgress>
    {
        public void Report(PluginProviderInstallProgress value)
        {
        }
    }

    /// <summary>Answers the registry, the release manifest of the CLI and their downloads.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);

        public List<string> Requested { get; } = [];

        public void AddRegistry(string antigravityVersion = "1.5.0")
        {
            var target = AcpInstallResolver.GetCurrentTargetId();
            _responses[RegistryUrl] = Encoding.UTF8.GetBytes($$"""
                {
                  "version": "1.0.0",
                  "agents": [
                    {
                      "id": "antigravity-acp", "name": "Google Antigravity", "version": "{{antigravityVersion}}", "description": "Antigravity",
                      "distribution": { "binary": { "{{target}}": { "archive": "{{RegistryArchiveUrl}}", "cmd": "./agy_acp_server.exe" } } }
                    },
                    {
                      "id": "other-agent", "name": "Other agent", "version": "1.2.0", "description": "Another agent",
                      "distribution": { "npx": { "package": "other-agent@1.2.0" } }
                    }
                  ]
                }
                """);
            _responses[RegistryArchiveUrl] = ServerBytes;
        }

        public void AddCli(string version, byte[] executable, string? sha512 = null)
        {
            // As Google releases it: the executable itself on Windows, an archive that holds `antigravity` elsewhere.
            var download = OperatingSystem.IsWindows() ? executable : Archive(executable);
            var url = OperatingSystem.IsWindows() ? "https://downloads.test/cli/cli_windows_x64.exe" : "https://downloads.test/cli/cli.tar.gz";
            _responses[url] = download;
            _responses[ManifestRoot + AntigravityCliReleases.GetPlatform() + ".json"] = Encoding.UTF8.GetBytes(
                $$"""{ "version": "{{version}}", "url": "{{url}}", "sha512": "{{sha512 ?? Convert.ToHexStringLower(SHA512.HashData(download))}}" }""");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requested)
            {
                Requested.Add(url);
            }

            return Task.FromResult(_responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static byte[] Archive(byte[] executable)
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
            using (var tar = new TarWriter(gzip))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "antigravity") { DataStream = new MemoryStream(executable) });
            }

            return buffer.ToArray();
        }
    }
}
