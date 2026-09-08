using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Tests;

/// <summary>Source-only contract wiring and complete, occurrence-checked pre-extraction reconstruction.</summary>
[TestClass]
public sealed class PluginUiContentExtractionSourceTests
{
    [TestMethod]
    public void Contracts_RegionContentIsNeutralAndTerminalTypesAreOptional()
    {
        var contracts = Read("CodeAlta.Plugins.Abstractions/PluginUiResourcesCompaction.cs");
        Assert.IsTrue(contracts.Contains("record PluginContentContribution : PluginUiContribution", StringComparison.Ordinal));
        Assert.IsFalse(contracts.Contains("Visual?", StringComparison.Ordinal));
        Assert.IsFalse(contracts.Contains("XenoAtom", StringComparison.Ordinal));
        Assert.IsTrue(contracts.Contains("required Func<PluginVisualContext, PluginRenderResult?> CreateContent", StringComparison.Ordinal));
        var optional = Read("CodeAlta.Plugins.Tui/PluginVisualContributions.cs");
        Assert.IsTrue(optional.Contains("PluginVisualContribution : PluginContentContribution", StringComparison.Ordinal));
        Assert.IsTrue(optional.Contains("PluginTerminalRendererContribution : PluginRendererContribution", StringComparison.Ordinal));
        Assert.IsFalse(Read("CodeAlta.Plugins/CodeAlta.Plugins.csproj").Contains("CodeAlta.Plugins.Tui", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Adapters_UseMandatoryRoutesAndExplicitTuiSelection()
    {
        var shared = Read("CodeAlta.Plugins/PluginContributionAdapters.cs");
        Assert.IsTrue(shared.Contains("PluginUiContentRouting.CreateContent", StringComparison.Ordinal));
        Assert.IsTrue(shared.Contains("PluginUiContentRouting.RenderAsync", StringComparison.Ordinal));
        Assert.IsTrue(shared.Contains(LegacyAsyncSignature, StringComparison.Ordinal));
        Assert.IsTrue(shared.Contains(LegacyAsyncForward, StringComparison.Ordinal));
        Assert.IsFalse(shared.Contains("IReadOnlyList<Visual>", StringComparison.Ordinal));
        var terminal = Read("CodeAlta.Tui/Plugins/TerminalPluginContributionAdapter.cs");
        Assert.IsTrue(terminal.Contains("_adapter.CreateContent", StringComparison.Ordinal));
        Assert.IsTrue(terminal.Contains("_adapter.RenderAsync", StringComparison.Ordinal));
        Assert.IsTrue(terminal.Contains("internal async ValueTask<(IReadOnlyList<PluginTerminalRenderResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> RenderAsync(", StringComparison.Ordinal));
        Assert.IsTrue(terminal.Contains("=> await _adapter.RenderAsync<PluginTerminalRenderResult>", StringComparison.Ordinal));
        Assert.IsTrue(terminal.Contains("SelectContent", StringComparison.Ordinal));
        Assert.IsTrue(terminal.Contains("SelectRenderer", StringComparison.Ordinal));
        Assert.IsTrue(terminal.Contains("var terminal = content as PluginVisualContribution;", StringComparison.Ordinal));
        Assert.IsTrue(terminal.Contains("return SelectContent(options?.SupportsTerminalVisuals == true, terminal is not null,", StringComparison.Ordinal));
        Assert.IsTrue(terminal.Contains("var terminal = renderer as PluginTerminalRendererContribution;", StringComparison.Ordinal));
        Assert.IsTrue(terminal.Contains("return SelectRenderer<PluginRendererContext, PluginTerminalRenderResult>(options?.SupportsTerminalVisuals == true,", StringComparison.Ordinal));
        Assert.IsTrue(Read("CodeAlta.Tui/App/PluginFrontendBridge.cs").Contains("SupportsTerminalVisuals = true", StringComparison.Ordinal));
        var routing = Read("CodeAlta.Plugins/PluginUiContentRouting.cs");
        Assert.IsTrue(routing.Contains("context.Invalidate();", StringComparison.Ordinal));
        Assert.IsTrue(routing.Contains("when (ex is not OperationCanceledException)", StringComparison.Ordinal));
        Assert.IsTrue(routing.IndexOf("var operation = createContext(registration);", StringComparison.Ordinal) < routing.IndexOf("try\n", StringComparison.Ordinal));
        Assert.IsTrue(routing.IndexOf("await render(renderer, context, cancellationToken)", StringComparison.Ordinal) < routing.IndexOf("context.Invalidate();", StringComparison.Ordinal));
        Assert.IsTrue(routing.IndexOf("context.Invalidate();", StringComparison.Ordinal) < routing.IndexOf("if (result is not null)", routing.IndexOf("context.Invalidate();", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.IsTrue(shared.Contains("LogCallbackFailure(active, \"Renderer contribution failed.\", ex);\n                return AddDiagnostic(CreateCallbackDiagnostic(registration, \"Renderer contribution failed.\", ex));", StringComparison.Ordinal));
        Assert.IsTrue(terminal.Contains("return supportsTerminal && hasTerminalPresentation ? direct ?? createNative?.Invoke(createContext()) : createPortable(createContext());", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SourceBuild_ConditionallyReferencesAndSharesOptionalAssembly()
    {
        var targets = Read("CodeAlta.Plugins/PluginRootBuildFiles.cs");
        Assert.IsTrue(targets.Contains("Exists('$(CodeAltaExeFolder)\\\\CodeAlta.Plugins.Tui.dll')", StringComparison.Ordinal));
        Assert.IsTrue(targets.Contains("CodeAlta.Plugins.Tui", StringComparison.Ordinal));
        Assert.IsTrue(targets.Contains("<Private>false</Private>", StringComparison.Ordinal));
        var loading = Read("CodeAlta.Plugins/PluginAssemblyLoading.cs");
        Assert.IsTrue(loading.Contains("\"CodeAlta.Plugins.Tui\"", StringComparison.Ordinal));
        Assert.IsFalse(loading.Contains("typeof(PluginTui)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Preservation_RestoresCompletePreExtractionSources()
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(InverseData));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip);
        Assert.AreEqual(17, document.RootElement.GetArrayLength());
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var path = entry.GetProperty("Path").GetString()!;
            foreach (var source in Representations(Read(path)))
            {
                var canonical = SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(source));
                canonical = path switch
                {
                    "CodeAlta.Plugins.Abstractions/PluginContributions.cs" => PluginKeyBindingExtractionSourceTests.RestorePreExtraction(path, canonical),
                    "CodeAlta.Plugin.Mcp/McpPlugin.cs" => PluginKeyBindingExtractionSourceTests.RestorePreExtraction(path, canonical),
                    _ => canonical,
                };
                if (path == "CodeAlta.Plugins.Abstractions/PluginFactories.cs")
                    canonical = PluginNeutralContractSourceInverse.Restore(path, canonical);
                if (path == "CodeAlta.Plugins/PluginContributionAdapters.cs")
                {
                    // Compose the legacy async-boundary correction with the frozen extraction inverses.
                    // Both complete declarations below must occur once; no baseline is rebased.
                    canonical = ReplaceOnce(canonical, LegacyAsyncSignature, LegacyForwardSignature, path);
                    canonical = ReplaceOnce(canonical, LegacyAsyncForward, LegacyForward, path);
                }
                foreach (var edit in entry.GetProperty("Edits").EnumerateArray())
                {
                    var after = SourceTestText.Canonicalize(edit.GetProperty("After").GetString()!);
                    var before = SourceTestText.Canonicalize(edit.GetProperty("Before").GetString()!);
                    canonical = ReplaceOnce(canonical, after, before, path);
                }
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(canonical)))));
                Assert.AreEqual(entry.GetProperty("Hash").GetString(), hash, path);
            }
        }
    }

    [TestMethod]
    public void Preservation_LeavesHistoricalChainsAndFrozenBoundariesUnchanged()
    {
        foreach (var (path, hash) in Frozen)
            Assert.AreEqual(hash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Read(path)))), path);
        var app = Read("CodeAlta.Tui/App/CodeAltaApp.cs");
        var appBytes = Encoding.UTF8.GetByteCount(app.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.AreEqual(47026, appBytes);
        Assert.IsTrue(appBytes < 47064);
        // This existing non-source policy file has no final newline; preserve it, rather than rewrite it.
        var attributes = SourceTestText.Canonicalize(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(SourcePath("../.gitattributes"))));
        Assert.AreEqual("4EAA6E29B30058F16275EDD79473C0312950E34A0A422961F58B050FA2668118",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(attributes))));
    }

    private static IEnumerable<string> Representations(string text)
    {
        yield return text;
        yield return text.Replace("\n", "\r\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        yield return string.Concat(lines.Select((line, index) => index == lines.Length - 1 ? line : line + (index % 2 == 0 ? "\r\n" : "\n")));
    }

    private static string Read(string path)
        => PluginAuthoringProfileSourceInverse.RestoreUiContentInput(path, PluginMcpBackendSeparationSourceInverse.RestoreUiContentInput(path, SourceTestText.DecodeSource(File.ReadAllBytes(SourcePath(path)))));

    private static string ReplaceOnce(string source, string after, string before, string path)
    {
        after = SourceTestText.Canonicalize(after);
        before = SourceTestText.Canonicalize(before);
        Assert.IsTrue(after.Length > 0, path);
        Assert.AreEqual(1, source.Split(after, StringSplitOptions.None).Length - 1, path);
        return source.Replace(after, before, StringComparison.Ordinal);
    }

    private const string LegacyAsyncSignature = "    public async ValueTask<(IReadOnlyList<PluginRenderResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> RenderAsync(\n";
    private const string LegacyForwardSignature = "    public ValueTask<(IReadOnlyList<PluginRenderResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> RenderAsync(\n";
    private const string LegacyAsyncForward = "        => await RenderAsync(activePlugins, region, target, payload, static (renderer, context, token) => renderer.Renderer(context, token), options, cancellationToken).ConfigureAwait(false);\n";
    private const string LegacyForward = "        => RenderAsync(activePlugins, region, target, payload, static (renderer, context, token) => renderer.Renderer(context, token), options, cancellationToken);\n";

    private static string SourcePath(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        return Path.Combine(directory.FullName, path);
    }

    // GZip UTF-8 JSON: 17 { Path, Hash, Edits: [{ Before, After }] } entries.
    // Before fragments are exact canonical c1fbc8d8 Git source, including all removed/moved declarations,
    // factories and traversal bodies. Every replacement must occur once; the entire reconstructed file
    // must match its frozen SHA-256. Compression only avoids duplicating large production blocks here.
    private const string InverseData = "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";

    private static IReadOnlyList<(string Path, string Hash)> Frozen =>
    [
        ("CodeAlta.Plugins/PluginStartupFeedback.cs", "920DDAE46E8EDD358E78EF48B91F6F8AFC6A66BCE99DF66EF2F2A68809EB0CE7"),
        ("CodeAlta.Plugins/PluginChangeNotifications.cs", "56F9328ADEE6AD40C511547A199E475B7283DD34F783E3087003CBB4BC326CA7"),
        ("CodeAlta.Plugins/PluginRuntimeManager.cs", "8BED175D3E2359CE8C4F21F07A0073FD145A20542F121A6B75580265A596102C"),
        ("CodeAlta.Plugins/CodeAlta.Plugins.csproj", "8B9474139C00CC76666FC90BF6065070F70607738CA4C35E7F34A0788A86331C"),
        ("CodeAlta.Tui/Program.cs", "2F3AA23636A7DC1BA69721DEFCF2918AAFA5FF163D93EFE17E1093AD37286753"),
        ("CodeAlta.Tui/App/CodeAltaOwnedServices.cs", "91BBD3719CE05B6326F386E11B9B3B0D7D492B95DFBDCE6E4BF9549BC07DCC6E"),
        ("CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs", "B0C8559B4597DE7212C618779AA8CB564E8BA336C1BCC848403C02609103D7DD"),
        ("CodeAlta.Orchestration/Hosting/CodeAltaHost.cs", "7F98979EC226FB9EEC7A9426340703E21301B5A04C82279B2C8C027EC6BA5078"),
        ("CodeAlta.Plugins.Tests/PluginStartupFeedbackReporterTests.cs", "BF8FEC135EE5D0F5AD18A13421C9788504EC0AD6DEA3A353179C0A15ECF98D47"),
        ("CodeAlta.Plugins.Tests/PluginChangeNotificationServiceTests.cs", "4746F0F039E7E20E86CA3682021AE9CF1B5A9C66184FE899C0A773A221DACE72"),
        ("CodeAlta.Tests/CodeAltaStartupAdmissionSourceTests.cs", "25F1637498D576F76BB734BF2C6BCD3B3BBBD1D1D13C07ADFE7B2B07285A77A3"),
        ("CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs", "EAEBDD92546584DE2B50A7C2B6B22A33B432ECEDE82C54C3D4963C497A1C397D"),
        ("CodeAlta.Tests/CodeAltaHostLifetimeTests.cs", "AC94374A336C80B9010482E5663B38221474FCC486F6E9A10F55248A6B421BDD"),
        ("CodeAlta.Tests/CodeAltaOwnedServicesLifetimeTests.cs", "EA4BB739A5A115C23CA0E511B2EF6427EF3CE6EA5E28E143E1ABB51042DA2CBF"),
        ("CodeAlta.Tests/PluginFeedbackExtractionSourceTests.cs", "CC9D9BDB7B56C1B7BBC1195C951E9943416DAE20B55EE7D80C137A36ECB3DE25"),
        ("CodeAlta.Tests/SourceTestText.cs", "4F6521081F2230CD679938741F5B62891F17EB7988C91B5E84A6A405A7E00563"),
        ("CodeAlta.Tests/ArchitectureGuardrailTests.cs", "321B5BD7F0C30F83E80D8E8EC3C06185DA779F49220B22BB37891821C0ECBBFD"),
        ("CodeAlta.Tests/CodeAltaFrontendCleanupSourceTests.cs", "B876880F4867472EFCC03BC27EB8C49C87CDE7F637411706FBCE160B2BB81FEF"),
        ("CodeAlta.Tests/FileEditorWorkspaceCleanupSourceTests.cs", "F8FA8629783572476E570FE65191A9DAFBDBC94AFAA63E121D93FD63A166A62D"),
        ("CodeAlta.Tests/ModelsDevCatalogLifetimeTests.cs", "DE9F5E136924E713467D80C998390F9EEF6FE4853E72C65D1146D32D5DBE6345"),
        ("CodeAlta.Tests/PromptDraftPrerequisiteSourceTests.cs", "1C8AE92FA92CB7C8250450F4DC29A9DE04199572721742208F9B1B5C3CEF3609"),
        ("CodeAlta.Tests/RuntimeEventPumpSourceTests.cs", "94DAF88C49D933909A332D0DB0FFA341370CCD996BCEBC7501F7756C28BFF46E"),
        ("CodeAlta.Tui/App/CodeAltaApp.cs", "B669F8C9336C66CDB6832F53E997F7BFC9C20916196944E64F967BC45CB6D2F2"),
        ("../doc/development-guide.md", "BCE3125D8F0C6F67F6EF136E0BEA68D071063F7688F4E9CB45340AD5E91DBC61"),
    ];
}
