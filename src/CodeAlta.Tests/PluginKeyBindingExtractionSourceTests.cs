using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Source-only wiring and strict whole-source inverses against 3825f1ac.</summary>
[TestClass]
public sealed class PluginKeyBindingExtractionSourceTests
{
    [TestMethod]
    public void Contracts_KeyBindingsAreNeutralAndMappingIsOptional()
    {
        var contracts = Read("CodeAlta.Plugins.Abstractions/PluginKeyBinding.cs");
        Assert.IsFalse(contracts.Contains("XenoAtom", StringComparison.Ordinal));
        Assert.IsTrue(contracts.Contains("public readonly record struct PluginKeyGesture", StringComparison.Ordinal));
        Assert.IsTrue(contracts.Contains("public sealed class PluginKeyBinding", StringComparison.Ordinal));
        Assert.IsTrue(contracts.Contains("Rune.IsLetter(character) ? Rune.ToUpperInvariant(character) : character", StringComparison.Ordinal));
        Assert.IsTrue(contracts.Contains("ReadOnlySpan<PluginKeyGesture> Gestures", StringComparison.Ordinal));
        var contributions = Read("CodeAlta.Plugins.Abstractions/PluginContributions.cs");
        Assert.IsFalse(contributions.Contains("DisplayText", StringComparison.Ordinal));
        Assert.IsFalse(contributions.Contains("XenoAtom.Terminal.UI.Input", StringComparison.Ordinal));
        Assert.IsTrue(contributions.Contains("PluginKeyBinding? KeyBinding", StringComparison.Ordinal));
        Assert.IsFalse(Read("CodeAlta.Plugins/CodeAlta.Plugins.csproj").Contains("CodeAlta.Plugins.Tui", StringComparison.Ordinal));
        var mapper = Read("CodeAlta.Plugins.Tui/PluginTerminalKeyBindingMapper.cs");
        Assert.IsTrue(mapper.Contains("public static bool TryMap(", StringComparison.Ordinal));
        Assert.IsFalse(mapper.Contains("TryParse", StringComparison.Ordinal));
        Assert.IsFalse(mapper.Contains("ToString(", StringComparison.Ordinal));
        Assert.IsFalse(mapper.Contains("(TerminalKey)", StringComparison.Ordinal));
        Assert.IsFalse(mapper.Contains("(TerminalModifiers)", StringComparison.Ordinal));
        Assert.IsTrue(mapper.Contains("TerminalModifiers.Meta", StringComparison.Ordinal));
        Assert.IsTrue(mapper.Contains("character.IsBmp", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Routes_UseActualConflictAndTerminalMappingSeams()
    {
        var registry = Read("CodeAlta.Plugins/PluginContributionRegistry.cs");
        Assert.IsTrue(registry.Contains(NewConflictSignature, StringComparison.Ordinal));
        Assert.IsTrue(registry.Contains(SourceTestText.Canonicalize(NewConflictBranches), StringComparison.Ordinal));
        Assert.IsTrue(registry.Contains(SourceTestText.Canonicalize(NewConflictHelper), StringComparison.Ordinal));
        Assert.IsTrue(registry.Contains("foreach (var key in GetConflictKeys(registration).DistinctBy", StringComparison.Ordinal));
        Assert.IsTrue(registry.Contains("GetConflictKeys(existing).Any", StringComparison.Ordinal));
        var adapter = Read("CodeAlta.Tui/Frontend/Commands/PluginShellCommandAdapter.cs");
        Assert.IsTrue(adapter.Contains("yield return CreateCommand(contribution);", StringComparison.Ordinal));
        Assert.IsTrue(adapter.Contains(SourceTestText.Canonicalize(NewAdapterStart), StringComparison.Ordinal));
        Assert.AreEqual(1, adapter.Split("PluginTerminalKeyBindingMapper.TryMap(", StringSplitOptions.None).Length - 1);
        Assert.IsTrue(adapter.Contains(NewAdapterForward, StringComparison.Ordinal));
        Assert.IsFalse(adapter.Contains("SupportsTerminalVisuals", StringComparison.Ordinal));
        Assert.IsFalse(adapter.Contains("HasInteractiveUi", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Preservation_RestoresCompletePreExtractionSources()
    {
        Assert.AreEqual(6, Originals.Count);
        foreach (var (path, _) in Originals)
        {
            foreach (var representation in Representations(Read(path)))
            {
                var canonical = SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(representation));
                RestorePreExtraction(path, canonical);
            }
        }
    }

    [TestMethod]
    public void Preservation_PreservesUiContentAndHistoricalChains()
    {
        var fixture = Read("CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs");
        Assert.IsTrue(fixture.Contains(SourceTestText.Canonicalize(NewUiDecodeChain), StringComparison.Ordinal));
        // Reconstruct the entire old fixture, including its frozen payload, hashes and async inverses.
        RestorePreExtraction("CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs", fixture);
        // These methods read source only: the 17 originals now compose both mandatory key inverses
        // before the untouched UI-content maps; the 24 frozen boundaries include Feedback14/earlier chains.
        var historical = new PluginUiContentExtractionSourceTests();
        historical.Preservation_RestoresCompletePreExtractionSources();
        historical.Preservation_LeavesHistoricalChainsAndFrozenBoundariesUnchanged();
    }

    internal static string RestorePreExtraction(string path, string source)
    {
        source = SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(source));
        (string Before, string After)[] edits = path switch
        {
            "CodeAlta.Plugins.Abstractions/PluginContributions.cs" =>
            [
                ("using XenoAtom.Terminal.UI;\nusing XenoAtom.Terminal.UI.Input;\n", "using XenoAtom.Terminal.UI;\n"),
                (OldBindingDeclaration + AvailabilityAnchor, AvailabilityAnchor),
            ],
            "CodeAlta.Plugins/PluginContributionRegistry.cs" =>
            [
                ("using CodeAlta.Plugins.Abstractions;\n", "using System.Globalization;\nusing System.Text;\nusing CodeAlta.Plugins.Abstractions;\n"),
                (OldConflictSignature, NewConflictSignature),
                (OldConflictBranches, NewConflictBranches),
                (LoadUnitAnchor, NewConflictHelper + LoadUnitAnchor),
                ("    private sealed record ContributionConflictKey(", "    internal sealed record ContributionConflictKey("),
            ],
            "CodeAlta.Tui/Frontend/Commands/PluginShellCommandAdapter.cs" =>
            [
                ("using CodeAlta.Plugins.Abstractions;\nusing XenoAtom.Terminal.UI;\n", "using CodeAlta.Plugins.Abstractions;\nusing CodeAlta.Plugins.Tui;\nusing XenoAtom.Terminal.UI;\nusing XenoAtom.Terminal.UI.Input;\n"),
                (OldAdapterStart, NewAdapterStart),
                (OldAdapterForward, NewAdapterForward),
            ],
            "CodeAlta.Plugin.Mcp/McpPlugin.cs" => [(OldMcpBinding, NewMcpBinding)],
            "CodeAlta.Plugins.Tests/PluginContributionRegistryTests.cs" =>
            [
                ("                KeyBinding = new PluginKeyBinding { DisplayText = \"Ctrl+X\" },\n", "                KeyBinding = new PluginKeyBinding(new PluginKeyGesture('X', PluginKeyModifiers.Ctrl)),\n"),
            ],
            "CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs" => [(OldUiDecode, NewUiDecodeChain)],
            _ => throw new AssertFailedException($"No mandatory key-binding inverse for {path}."),
        };
        foreach (var (before, after) in edits)
        {
            var canonicalAfter = SourceTestText.Canonicalize(after);
            Assert.IsTrue(canonicalAfter.Length > 0, path);
            Assert.AreEqual(1, source.Split(canonicalAfter, StringSplitOptions.None).Length - 1, path);
            source = source.Replace(canonicalAfter, SourceTestText.Canonicalize(before), StringComparison.Ordinal);
        }
        var restored = SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(source));
        Assert.AreEqual(Originals.Single(entry => entry.Path == path).Hash,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(restored))), path);
        return restored;
    }

    private static IEnumerable<string> Representations(string text)
    {
        yield return text;
        yield return text.Replace("\n", "\r\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        yield return string.Concat(lines.Select((line, index) => index == lines.Length - 1 ? line : line + (index % 2 == 0 ? "\r\n" : "\n")));
    }

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        return SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(directory.FullName, path)));
    }

    // Full canonical Git originals at 3825f1ac, not hashes of selected declarations or normalized whitespace.
    private static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        ("CodeAlta.Plugins.Abstractions/PluginContributions.cs", "3C14A9FAF5A2B1EBFDF5B129E64B247A9A8B93FBF21A764CE6D2B4CA2276E664"),
        ("CodeAlta.Plugins/PluginContributionRegistry.cs", "BA07DD63DE0637335D52821F86C6D9E978127E39C7C40853158385E60D38F061"),
        ("CodeAlta.Tui/Frontend/Commands/PluginShellCommandAdapter.cs", "E56B0221CD2B42455B68DCC62211D0761ADDEFBF5E72EEC4122526AFCB1DF288"),
        ("CodeAlta.Plugin.Mcp/McpPlugin.cs", "7DE305EB82DBAD6AA0BFC4A1CE9E47323FA8AFABD7B436DC37B38BEAD547B611"),
        ("CodeAlta.Plugins.Tests/PluginContributionRegistryTests.cs", "95C3BB6B237F3EA279C95287F89BF80180692506AF13DDC92921775918A2F2C0"),
        ("CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs", "C1A73042F5C63A4FBB889EE94F45A40EEA6749C9B8B8B32CD99E3FFDD0C107FB"),
    ];

    private const string AvailabilityAnchor = "/// <summary>Describes when a command is available.</summary>\n";
    private const string OldBindingDeclaration = """
        /// <summary>Describes a plugin key binding.</summary>
        public sealed record PluginKeyBinding
        {
            /// <summary>Gets display text for the binding.</summary>
            public required string DisplayText { get; init; }

            /// <summary>Gets a single-key gesture.</summary>
            public KeyGesture? Gesture { get; init; }

            /// <summary>Gets a multi-key sequence.</summary>
            public KeySequence? Sequence { get; init; }
        }

        """ + "\n";

    private const string OldConflictSignature = "    private static IEnumerable<ContributionConflictKey> GetConflictKeys(PluginContributionRegistration registration)\n";
    private const string NewConflictSignature = "    internal static IEnumerable<ContributionConflictKey> GetConflictKeys(PluginContributionRegistration registration)\n";
    private const string OldConflictBranches = """
                        if (command.KeyBinding?.DisplayText is { Length: > 0 } displayText)
                        {
                            yield return new ContributionConflictKey("keybinding", $"keybinding:{displayText}", displayText);
                        }

                        if (command.KeyBinding?.Gesture is { } gesture)
                        {
                            yield return new ContributionConflictKey("keybinding", $"keybinding-gesture:{gesture}", gesture.ToString() ?? string.Empty);
                        }

                        if (command.KeyBinding?.Sequence is { } sequence)
                        {
                            yield return new ContributionConflictKey("keybinding", $"keybinding-sequence:{sequence}", sequence.ToString() ?? string.Empty);
                        }
        """ + "\n";
    private const string NewConflictBranches = """
                        if (command.KeyBinding is { } binding)
                        {
                            yield return new ContributionConflictKey("keybinding", GetBindingConflictKey(binding), binding.ToString());
                        }
        """ + "\n";
    private const string LoadUnitAnchor = "    private static PluginLoadUnitKind GetLoadUnitKind(PluginDescriptor descriptor)\n";
    private const string NewConflictHelper = """
            private static string GetBindingConflictKey(PluginKeyBinding binding)
            {
                var builder = new StringBuilder("keybinding:");
                builder.Append(binding.Count.ToString(CultureInfo.InvariantCulture)).Append(':');
                foreach (var stroke in binding.Gestures)
                {
                    builder.Append(stroke.Character is null ? 'N' : 'U');
                    var identity = stroke.Character is { } character ? character.Value : (int)stroke.Key;
                    builder.Append(identity.ToString("X6", CultureInfo.InvariantCulture)).Append('/');
                    builder.Append(((int)stroke.Modifiers).ToString("X2", CultureInfo.InvariantCulture)).Append(';');
                }
                return builder.ToString();
            }

        """ + "\n";
    private const string OldAdapterStart = """
            private static ShellCommand CreateCommand(PluginCommandContribution contribution)
            {
                ArgumentNullException.ThrowIfNull(contribution);
                return new ShellCommand
        """ + "\n";
    private const string NewAdapterStart = """
            internal static ShellCommand CreateCommand(PluginCommandContribution contribution)
            {
                ArgumentNullException.ThrowIfNull(contribution);
                KeyGesture? gesture = null;
                KeySequence? sequence = null;
                if (contribution.KeyBinding is { } binding)
                {
                    PluginTerminalKeyBindingMapper.TryMap(binding, out gesture, out sequence);
                }
                return new ShellCommand
        """ + "\n";
    private const string OldAdapterForward = "            Gesture = contribution.KeyBinding?.Gesture,\n            Sequence = contribution.KeyBinding?.Sequence,\n";
    private const string NewAdapterForward = "            Gesture = gesture,\n            Sequence = sequence,\n";
    private const string OldMcpBinding = """
            private static readonly PluginKeyBinding ManageServersKeyBinding = new()
            {
                DisplayText = "Ctrl+G Ctrl+Y",
                Sequence = new KeySequence(
                    new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
                    new KeyGesture(TerminalChar.CtrlY, TerminalModifiers.Ctrl)),
            };
        """ + "\n";
    private const string NewMcpBinding = """
            private static readonly PluginKeyBinding ManageServersKeyBinding = new(
                new PluginKeyGesture('G', PluginKeyModifiers.Ctrl),
                new PluginKeyGesture('Y', PluginKeyModifiers.Ctrl));
        """ + "\n";
    private const string OldUiDecode = "                var canonical = SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(source));\n";
    private const string NewUiDecodeChain = """
                        var canonical = SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(source));
                        canonical = path switch
                        {
                            "CodeAlta.Plugins.Abstractions/PluginContributions.cs" => PluginKeyBindingExtractionSourceTests.RestorePreExtraction(path, canonical),
                            "CodeAlta.Plugin.Mcp/McpPlugin.cs" => PluginKeyBindingExtractionSourceTests.RestorePreExtraction(path, canonical),
                            _ => canonical,
                        };
        """ + "\n";
}
