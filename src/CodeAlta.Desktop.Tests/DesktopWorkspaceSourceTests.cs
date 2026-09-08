using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Tests;

namespace CodeAlta.Desktop.Tests;

/// <summary>Named source reads only; not catalog, native bridge or lifetime qualification.</summary>
[TestClass]
public sealed class DesktopWorkspaceSourceTests
{
    [TestMethod]
    public void Composition_UsesOnlyExplicitCatalogReads()
    {
        var rpc = Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs");
        StringAssert.Contains(rpc, "new ProjectCatalog(options)");
        StringAssert.Contains(rpc, "new SessionViewJournalStore(options)");
        StringAssert.Contains(rpc, "IAgentSessionCatalog sessions = new AgentSessionCatalog(journals.CreateSessionStore())");
        StringAssert.Contains(rpc, "projects.LoadAsync");
        StringAssert.Contains(rpc, "sessions.ListSessionsAsync(filter: null, cancellationToken: token)");
        foreach (var forbidden in new[] { "ListHeadersAsync", "ReadLatestStateAsync", "CodeAltaHost", "InvalidateAsync", "Environment.", "Task.Run(", "new CancellationTokenSource" })
            Assert.IsFalse(rpc.Contains(forbidden, StringComparison.Ordinal), forbidden);
        var app = Read("CodeAlta/Desktop/DesktopApplication.cs");
        StringAssert.Contains(app, "builder.AddWorkspaceService(new WorkspaceService(options.CatalogRoot));");
        var cli = Read("CodeAlta/Desktop/DesktopCommandLine.cs");
        StringAssert.Contains(cli, "TryParse(args, Directory.Exists, File.Exists, out var options, out var message)");
        StringAssert.Contains(cli, "return startNative(options!);");
    }

    [TestMethod]
    public void Rpc_UsesGeneratedContractAndActualReadSeam()
    {
        var rpc = Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs");
        StringAssert.Contains(rpc, "[NeoRpcService(\"workspace\", Version = 1)]");
        StringAssert.Contains(rpc, "[NeoRpcMethod(\"snapshot\")]");
        StringAssert.Contains(rpc, "internal sealed record WorkspaceRequest;");
        StringAssert.Contains(rpc, "ReadAsync(projects.LoadAsync,");
        StringAssert.Contains(rpc, "await foreach (var session in loadSessions(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))");
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/BootRpc.cs"), "[JsonSerializable(typeof(WorkspaceSnapshot))]");
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/BootRpc.cs"), "DesktopCommandLine.Version, false)");
        StringAssert.Contains(Read("CodeAlta/frontend/src/main.tsx"), "loadWorkspace(workspace.snapshot, abort.signal, setWorkspaceState)");
        StringAssert.Contains(Read("CodeAlta/frontend/src/main.tsx"), "boot.status(");
    }

    [TestMethod]
    public void Frontend_UsesRealRpcAndRendersAllStates()
    {
        var main = Read("CodeAlta/frontend/src/main.tsx");
        foreach (var text in new[] { "#neoastra", "role=\"status\"", "role=\"alert\"", "unconfigured", "loading", "error", "ready", "Global / unmatched", "No persisted sessions", "sessionsForProject(", "workspaceNotice(", "abort.abort()" })
            StringAssert.Contains(main, text);
        var helper = Read("CodeAlta/frontend/src/workspace.ts");
        StringAssert.Contains(helper, "if (signal.aborted) return;");
        StringAssert.Contains(helper, "await invoke({}, { signal, timeoutMilliseconds: 30_000 })");
        Assert.IsFalse(main.Contains("dangerouslySetInnerHTML", StringComparison.Ordinal));
        Assert.IsFalse(main.Contains("Refresh", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Boundaries_PreserveTrustAndDocumentReadLimits()
    {
        using var stream = new MemoryStream(Convert.FromBase64String(InverseData));
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip);
        Assert.AreEqual(9, document.RootElement.GetArrayLength());
        CollectionAssert.AreEquivalent(Originals.Select(value => value.Path).ToArray(),
            document.RootElement.EnumerateArray().Select(value => value.GetProperty("Path").GetString()!).ToArray());
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var path = entry.GetProperty("Path").GetString()!;
            Assert.AreEqual(Originals.Single(value => value.Path == path).Hash, entry.GetProperty("Hash").GetString());
            foreach (var representation in Representations(Read(path)))
            {
                var source = SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(representation));
                Assert.IsTrue(entry.GetProperty("Edits").GetArrayLength() > 0, path);
                foreach (var edit in entry.GetProperty("Edits").EnumerateArray())
                {
                    var after = SourceTestText.Canonicalize(edit.GetProperty("After").GetString()!);
                    var before = SourceTestText.Canonicalize(edit.GetProperty("Before").GetString()!);
                    Assert.IsTrue(after.Length > 0, path);
                    Assert.AreEqual(1, source.Split(after, StringSplitOptions.None).Length - 1, path);
                    source = source.Replace(after, before, StringComparison.Ordinal);
                }
                Assert.AreEqual(entry.GetProperty("Hash").GetString(), Hash(SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(source))), path);
            }
        }
        foreach (var (path, hash) in Frozen) Assert.AreEqual(hash, Hash(Read(path)), path);
        var appBytes = Encoding.UTF8.GetByteCount(Read("CodeAlta.Tui/App/CodeAltaApp.cs").Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.AreEqual(47026, appBytes);
        Assert.IsTrue(appBytes < 47064);
        // Existing attributes intentionally have no final newline.
        var attributes = SourceTestText.Canonicalize(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, "../.gitattributes"))));
        Assert.AreEqual("4EAA6E29B30058F16275EDD79473C0312950E34A0A422961F58B050FA2668118", Hash(attributes));
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs"), "five seconds");
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs"), "CancellationToken.None");
        StringAssert.Contains(Read("CodeAlta/frontend/src/main.tsx"), "cache/cache.sqlite3");
    }

    private static string Read(string path) => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static IEnumerable<string> Representations(string text)
    {
        yield return text;
        yield return text.Replace("\n", "\r\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        yield return string.Concat(lines.Select((line, i) => i == lines.Length - 1 ? line : line + (i % 2 == 0 ? "\r\n" : "\n")));
    }

    // Frozen before any production edit: entire strict canonical Git sources at e2a097fa.
    private static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        ("CodeAlta/CodeAlta.csproj", "6A4C58C8B28386D66109FAF8A67745A8E2CA1BD9D631D5D6835F043F8824EE31"),
        ("CodeAlta/Desktop/DesktopCommandLine.cs", "7DA8D37952F8FAB54CDA9B220C9300E2B5A32C470F06A68409350CC9A3C3B46F"),
        ("CodeAlta/Desktop/DesktopApplication.cs", "14AF57C9269E07F0C6706A1C5BE3CB01EA0F34B86E1C10A094E97C61312E5CB2"),
        ("CodeAlta/Desktop/Rpc/BootRpc.cs", "D09F3039A50C056CB7CB581E6F8B7315FFFB1317522821C659EA10ACF1BD8500"),
        ("CodeAlta/frontend/src/main.tsx", "ADDA6F69491EBB16126BB60EEC4DC4929AEC0D2169C1DD61D2164855C043B763"),
        ("CodeAlta/frontend/src/style.css", "33BEDA2D714320BFC33162CB30596263F106BC2A34B529641DD70D4076552BEB"),
        ("CodeAlta.Desktop.Tests/DesktopStartupTests.cs", "E50E2BA7B4B8EB1AEDB616510B823C791A3F0A3FFD6DA8B710E57137CFC5D2E5"),
        ("CodeAlta.Desktop.Tests/DesktopArchitectureTests.cs", "FC03E487FB5041781AC95B4E1595E4310CEDC652FE601030755A03985B517191"),
        ("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj", "E5FBA82DA5D6DA41B4323E9F239F219636A8939692CA3995F89EA51345A59B0C"),
    ];

    private static IReadOnlyList<(string Path, string Hash)> Frozen =>
    [
        ("CodeAlta.Tests/SourceTestText.cs", "4F6521081F2230CD679938741F5B62891F17EB7988C91B5E84A6A405A7E00563"),
        ("CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs", "12CC4C7C67A120AA96BE003D8DF6058F1B36AFAF2025A4594B9DCDCD1983B68C"),
        ("CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs", "0D45FF57359D5A6AE0EDA352EF112384793DD451355A510B6B918F85EE11AF44"),
        ("CodeAlta.Tests/PluginFeedbackExtractionSourceTests.cs", "CC9D9BDB7B56C1B7BBC1195C951E9943416DAE20B55EE7D80C137A36ECB3DE25"),
        ("CodeAlta.Tui/App/CodeAltaApp.cs", "B669F8C9336C66CDB6832F53E997F7BFC9C20916196944E64F967BC45CB6D2F2"),
        ("../doc/development-guide.md", "BCE3125D8F0C6F67F6EF136E0BEA68D071063F7688F4E9CB45340AD5E91DBC61"),
    ];

    // GZip UTF-8 JSON of nine { Path, Hash, Edits: [{ Before, After }] } entries.
    // Full context-bearing inverse fragments, including removed source and old architecture guards.
    // Thirteen context-bearing edits, captured against the originals frozen above; hashes are never rebased.
    private const string InverseData = "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";
}
