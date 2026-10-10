using System.Security;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentInstructionFileTests
{
    [TestMethod]
    [DataRow("metadata", "denied")]
    [DataRow("metadata", "io")]
    [DataRow("metadata", "security")]
    [DataRow("read", "denied")]
    [DataRow("read", "io")]
    [DataRow("read", "security")]
    [DataRow("read", "missing-file")]
    [DataRow("read", "missing-directory")]
    public void UnavailableInstructions_PreserveAccessibleParentsChildrenAndSelection(string stage, string failure)
    {
        using var fixture = new InstructionFixture();
        foreach (var orchestration in new[] { false, true })
        {
            var reads = new List<string>();
            var reader = new AgentInstructionFileReader(
                path => path == fixture.Blocked && stage == "metadata" ? throw Failure(failure) : fixture.GetLength(path),
                path =>
                {
                    reads.Add(path);
                    return path == fixture.Blocked && stage == "read" ? throw Failure(failure) : fixture.Content[path];
                });
            var text = fixture.Compose(orchestration, reader);

            Assert.IsNotNull(text);
            var parent = text.IndexOf("Accessible ancestor.", StringComparison.Ordinal);
            var child = text.IndexOf("Accessible child.", StringComparison.Ordinal);
            Assert.IsTrue(parent >= 0 && child > parent, text);
            Assert.IsFalse(text.Contains("Unavailable guidance.", StringComparison.Ordinal));
            // Metadata failure excludes only that candidate. Once selected, a read failure never promotes
            // a smaller sibling that the normal precedence rules would have excluded.
            Assert.AreEqual(stage == "metadata", text.Contains("Accessible sibling.", StringComparison.Ordinal));
            Assert.AreEqual(stage == "metadata", reads.Contains(fixture.Sibling));
            Assert.AreEqual(1, reader.Diagnostics.Count, "Overlapping working/project roots must not duplicate a warning.");
            var diagnostic = reader.Diagnostics.Single();
            Assert.AreEqual(fixture.Blocked, diagnostic.Path);
            StringAssert.Contains(diagnostic.Message, Failure(failure).Message);
            StringAssert.Contains(diagnostic.Message, "guidance is unavailable");
            Assert.AreEqual(1, reads.Count(path => path == fixture.Ancestor));
            Assert.AreEqual(1, reads.Count(path => path == fixture.Child));
        }
    }

    [TestMethod]
    public void NormalInstructions_KeepExactContentOrderHashAndManifestWithoutWarnings()
    {
        using var fixture = new InstructionFixture();
        var raw = AgentInstructionComposer.Compose(fixture.Options, null, fixture.Reader());
        Assert.AreEqual(string.Join(Environment.NewLine + Environment.NewLine,
            "Developer guidance.",
            $"File: {fixture.Ancestor}{Environment.NewLine}Accessible ancestor.",
            $"File: {fixture.Blocked}{Environment.NewLine}Unavailable guidance.",
            $"File: {fixture.Child}{Environment.NewLine}Accessible child."), raw.DeveloperInstructions);
        Assert.AreEqual("System guidance.", raw.SystemMessage);
        Assert.AreEqual(0, raw.Diagnostics.Count);
        Assert.AreEqual(raw.InstructionHash, AgentInstructionComposer.Compose(fixture.Options, null, fixture.Reader()).InstructionHash);

        var bundle = fixture.Builder.Build(fixture.Request, fixture.Reader());
        Assert.AreEqual(string.Join(Environment.NewLine + Environment.NewLine,
            "# Agent Prompt" + Environment.NewLine + Environment.NewLine + "Fixture prompt.",
            "# Project Context" + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine,
                new[] { fixture.Ancestor, fixture.Blocked, fixture.Child }.Select(path =>
                    $"File: `{path}`{Environment.NewLine}{Environment.NewLine}<INSTRUCTIONS>{Environment.NewLine}{Environment.NewLine}{fixture.Content[path].Trim()}{Environment.NewLine}{Environment.NewLine}</INSTRUCTIONS>"))), bundle.DeveloperInstructions);
        CollectionAssert.AreEqual(new[] { fixture.Ancestor, fixture.Blocked, fixture.Child }, bundle.Manifest.Parts.Single(part => part.Key == "project.context").SourcePaths!.ToArray());
        Assert.AreEqual(0, bundle.Diagnostics.Count);
        Assert.AreEqual(bundle.EffectivePromptHash, fixture.Builder.Build(fixture.Request, fixture.Reader()).EffectivePromptHash);
    }

    [TestMethod]
    public void Discovery_MissingOptionalCandidatesAreQuietAndTiesKeepOrdinalPathSelection()
    {
        using var fixture = new InstructionFixture();
        var reader = new AgentInstructionFileReader(
            path => path == fixture.Blocked || path == fixture.Sibling ? 5 : throw new FileNotFoundException(),
            path => fixture.Content[path]);
        Assert.AreEqual(fixture.Blocked, reader.SelectLargestFile(Path.GetDirectoryName(fixture.Blocked)!));
        Assert.IsNull(reader.SelectLargestFile(fixture.Root));
        Assert.AreEqual(0, reader.Diagnostics.Count);
    }

    [TestMethod]
    public void SelectedFile_DisappearingDuringMetadataRecheckIsReported()
    {
        using var fixture = new InstructionFixture();
        var probes = 0;
        var reader = new AgentInstructionFileReader(path =>
        {
            // The second probe is the selected-file size check used for the large-context warning.
            if (path == fixture.Blocked && ++probes == 2) throw new FileNotFoundException("Removed after discovery.");
            return fixture.GetLength(path);
        }, path => fixture.Content[path]);
        var bundle = fixture.Builder.Build(fixture.Request, reader);
        Assert.IsFalse(bundle.DeveloperInstructions!.Contains("Unavailable guidance.", StringComparison.Ordinal));
        Assert.AreEqual(fixture.Blocked, bundle.Diagnostics.Single().Path);
        StringAssert.Contains(bundle.Diagnostics.Single().Message, "Removed after discovery.");
    }

    [TestMethod]
    public void Composition_DoesNotSwallowCancellationOrProgrammingErrors()
    {
        using var fixture = new InstructionFixture();
        foreach (var orchestration in new[] { false, true })
        {
            Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Compose(orchestration,
                new AgentInstructionFileReader(_ => throw new OperationCanceledException(), _ => "unused")));
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Compose(orchestration,
                new AgentInstructionFileReader(fixture.GetLength, _ => throw new InvalidOperationException())));
        }
    }

    [TestMethod]
    public void AlreadyComposedInstructions_DoNotProbeOrReadFiles()
    {
        using var fixture = new InstructionFixture();
        var reader = new AgentInstructionFileReader(_ => throw new AssertFailedException("Unexpected metadata read."),
            _ => throw new AssertFailedException("Unexpected content read."));
        var bundle = AgentInstructionComposer.Compose(new AgentSessionCreateOptions
        {
            WorkingDirectory = fixture.Options.WorkingDirectory, ProjectRoots = fixture.Options.ProjectRoots,
            DeveloperInstructions = fixture.Options.DeveloperInstructions, InstructionsAlreadyComposed = true,
            OnPermissionRequest = fixture.Options.OnPermissionRequest,
        }, null, reader);
        Assert.AreEqual("Developer guidance.", bundle.DeveloperInstructions);
        Assert.AreEqual(0, bundle.Diagnostics.Count);
    }

    [TestMethod]
    public void Discovery_RealAbsentFilesAndDirectoryNamesAreNotInstructions()
    {
        using var temp = TestTempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(temp.Path, "AGENTS.md"));
        var reader = new AgentInstructionFileReader();
        Assert.IsNull(reader.SelectLargestFile(temp.Path));
        Assert.AreEqual(0, reader.Diagnostics.Count);
        File.WriteAllText(Path.Combine(temp.Path, "CLAUDE.md"), "Readable guidance.");
        Assert.AreEqual(Path.Combine(temp.Path, "CLAUDE.md"), reader.SelectLargestFile(temp.Path));
    }

    [TestMethod]
    public void EmptySelectedInstructions_StillDoNotPromoteSmallerSiblings()
    {
        using var fixture = new InstructionFixture();
        fixture.Content[fixture.Blocked] = new string(' ', 100);
        foreach (var orchestration in new[] { false, true })
        {
            var reader = fixture.Reader();
            var text = fixture.Compose(orchestration, reader);
            StringAssert.Contains(text!, "Accessible ancestor.");
            StringAssert.Contains(text!, "Accessible child.");
            Assert.IsFalse(text!.Contains("Accessible sibling.", StringComparison.Ordinal));
            Assert.AreEqual(0, reader.Diagnostics.Count);
        }
    }

    private static Exception Failure(string kind) => kind switch
    {
        "denied" => new UnauthorizedAccessException("Synthetic access denial."),
        "io" => new IOException("Synthetic I/O failure."),
        "security" => new SecurityException("Synthetic sandbox denial."),
        "missing-file" => new FileNotFoundException("Synthetic removed file."),
        "missing-directory" => new DirectoryNotFoundException("Synthetic removed directory."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class InstructionFixture : IDisposable
    {
        private readonly TestTempDirectory _temp = TestTempDirectory.Create();
        internal string Root => _temp.Path;
        internal string Ancestor { get; }
        internal string Blocked { get; }
        internal string Sibling { get; }
        internal string Child { get; }
        internal Dictionary<string, string> Content { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal AgentSessionCreateOptions Options { get; }
        internal SystemPromptBuilder Builder { get; }
        internal SystemPromptBuildRequest Request { get; }

        internal InstructionFixture()
        {
            var project = Directory.CreateDirectory(Path.Combine(Root, "parent", "child")).FullName;
            Ancestor = Path.Combine(Root, "AGENTS.md");
            Blocked = Path.Combine(Root, "parent", "AGENTS.md");
            Sibling = Path.Combine(Root, "parent", "CLAUDE.md");
            Child = Path.Combine(project, "AGENTS.md");
            Content[Ancestor] = "  Accessible ancestor.  ";
            Content[Blocked] = "Unavailable guidance.";
            Content[Sibling] = "Accessible sibling.";
            Content[Child] = "Accessible child.";
            Options = new()
            {
                WorkingDirectory = project, ProjectRoots = [project], SystemMessage = " System guidance. ",
                DeveloperInstructions = " Developer guidance. ",
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
            };
            var shipped = Path.Combine(Root, "app", "content", "prompts");
            Directory.CreateDirectory(Path.Combine(shipped, "agents"));
            Directory.CreateDirectory(Path.Combine(shipped, "system"));
            File.WriteAllText(Path.Combine(shipped, "agents", "default.prompt.md"), "---\nname: Fixture\n---\nFixture prompt.");
            File.WriteAllText(Path.Combine(shipped, "system", "default.system-prompt.md"), "Fixture system.");
            Builder = new(new FileSystemPromptContentLocator(Path.Combine(Root, "app")));
            Request = new()
            {
                ProviderKey = "inert", ProviderType = "inert", ProtocolFamily = "inert",
                Session = new() { SessionId = "inert", ProviderId = "inert", WorkingDirectory = project },
                ProjectRoots = [project], DiscoveryScope = new(Path.Combine(Root, "home"), Root),
                UserCodeAltaRoot = Path.Combine(Root, "global"),
                PartOptionsOverride = new(Skills: false, ProjectContext: true, RuntimeContext: false, ToolGuidance: false),
            };
        }

        internal long GetLength(string path) => Content.TryGetValue(path, out var text) ? text.Length : throw new FileNotFoundException();
        internal AgentInstructionFileReader Reader() => new(GetLength, path => Content[path]);
        internal string? Compose(bool orchestration, AgentInstructionFileReader reader)
        {
            if (!orchestration) return AgentInstructionComposer.Compose(Options, null, reader).DeveloperInstructions;
            var bundle = Builder.Build(Request, reader);
            CollectionAssert.AreEqual(reader.Diagnostics.Select(item => item.Path).ToArray(), bundle.Diagnostics.Select(item => item.Path).ToArray());
            Assert.IsTrue(bundle.Diagnostics.All(item => item.Severity == SystemPromptDiagnosticSeverity.Warning && item.Code == "unreadable_project_context_file"));
            return bundle.DeveloperInstructions;
        }

        public void Dispose() => _temp.Dispose();
    }
}
