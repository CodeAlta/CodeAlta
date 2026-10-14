using System.Text.Json;
using CodeAlta.Agent.Claude;

namespace CodeAlta.Tests;

/// <summary>
/// The folders where Claude Code keeps the conversations of a working directory, and the move of a conversation to
/// the folder of another working directory.
/// </summary>
[TestClass]
public sealed class ClaudeCodeTranscriptRelocationTests
{
    private const string SessionId = "0a1b2c3d-4e5f-6789-abcd-ef0123456789";
    private const string OldDirectory = @"C:\work\old";
    private const string NewDirectory = @"C:\work\new";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-claude-relocation-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.Zero));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            // A linked folder is removed as a link, before the folder it points to.
            foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                .Where(static folder => new DirectoryInfo(folder).LinkTarget is not null).ToList())
            {
                Directory.Delete(link);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(@"C:\dev2\CodeAlta", "C--dev2-CodeAlta")]
    [DataRow(@"C:\Users\xen\.alta", "C--Users-xen--alta")]
    [DataRow("folder.B", "folder-B")]
    [DataRow(@"c:\dev2\CodeAlta", "c--dev2-CodeAlta")]
    [DataRow("/home/user/project/", "-home-user-project")]
    [DataRow("/home/user/my project", "-home-user-my-project")]
    [DataRow("C:\\caf\u00e9", "C--caf-")]
    public void FolderName_ReplacesEveryOtherCharacterThanLettersAndDigits(string workingDirectory, string expected)
    {
        Assert.AreEqual(expected, ClaudeCodeProjectFolders.GetFolderName(workingDirectory));
    }

    [TestMethod]
    public void FolderName_ComposesTheDecomposedFormOfAPath()
    {
        // The CLI hashes and names the NFC form: "e" followed by a combining acute is one character there.
        Assert.AreEqual("C--caf-", ClaudeCodeProjectFolders.GetFolderName("C:\\cafe\u0301"));
    }

    [TestMethod]
    public void FolderName_CutsALongNameAndAddsTheHashOfThePath()
    {
        // Expected values computed by the code of the CLI, run in node.
        var path = @"C:\work\" + string.Concat(Enumerable.Repeat("very-long-folder-name.", 10)) + @"\project";
        Assert.AreEqual(236, path.Length);
        Assert.AreEqual(-1083596593, ClaudeCodeProjectFolders.HashCode(path));
        Assert.AreEqual(
            "C--work-very-long-folder-name-very-long-folder-name-very-long-folder-name-very-long-folder-name-very-long-folder-name-very-long-folder-name-very-long-folder-name-very-long-folder-name-very-long-folder-hx58hd",
            ClaudeCodeProjectFolders.GetFolderName(path));

        var other = @"D:\" + string.Concat(Enumerable.Repeat(@"abcdefghij\", 20)) + "end";
        Assert.AreEqual(
            "D--abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-47539n",
            ClaudeCodeProjectFolders.GetFolderName(other));
    }

    [TestMethod]
    public void FolderName_KeepsAShortNameWhole()
    {
        var path = "/" + new string('a', ClaudeCodeProjectFolders.MaxNameLength - 1);

        Assert.AreEqual("-" + new string('a', ClaudeCodeProjectFolders.MaxNameLength - 1), ClaudeCodeProjectFolders.GetFolderName(path));
    }

    [TestMethod]
    public void Base36_WritesTheAbsoluteValueOfTheSmallestHash()
    {
        // Math.abs of the smallest 32-bit value is 2^31 in JavaScript: it does not overflow.
        Assert.AreEqual("zik0zk", ClaudeCodeProjectFolders.ToBase36(Math.Abs((long)int.MinValue)));
        Assert.AreEqual("0", ClaudeCodeProjectFolders.ToBase36(0));
    }

    [TestMethod]
    public void ConfigRoot_IsTheVariableElseTheFolderInHome()
    {
        Assert.AreEqual(@"D:\claude", ClaudeCodeProjectFolders.ResolveConfigRoot(name => name == "CLAUDE_CONFIG_DIR" ? @"D:\claude" : null));
        Assert.AreEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            ClaudeCodeProjectFolders.ResolveConfigRoot(_ => " "));
    }

    [TestMethod]
    public void ReadCliSessionId_ReadsTheStateOfClaudeCode()
    {
        var state = new ClaudeCodeProviderState(SessionId, 2, 2).ToJson();

        Assert.AreEqual(SessionId, ClaudeCodeTranscriptRelocation.ReadCliSessionId(state));
        Assert.IsNull(ClaudeCodeTranscriptRelocation.ReadCliSessionId(null));
        Assert.IsNull(ClaudeCodeTranscriptRelocation.ReadCliSessionId(JsonDocument.Parse("""{"kind":"codex","sessionId":"x"}""").RootElement));
    }

    [TestMethod]
    [DataRow("..")]
    [DataRow(@"..\escape")]
    [DataRow("a/b")]
    [DataRow(@"a\b")]
    [DataRow(" id")]
    [DataRow("")]
    public async Task Relocate_RejectsAnIdentifierThatIsNotAFileName(string sessionId)
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => ClaudeCodeTranscriptRelocation.RelocateAsync(_root, sessionId, OldDirectory, NewDirectory, CancellationToken.None));
    }

    [TestMethod]
    public async Task Relocate_MovesTheTranscriptAndItsCompanionFolder()
    {
        var source = WriteTranscript(OldDirectory, "line 1\nline 2\n");
        var companion = Path.Combine(Path.GetDirectoryName(source)!, SessionId);
        WriteFile(Path.Combine(companion, "tool-results", "result.txt"), "result");
        WriteFile(Path.Combine(companion, "subagents", "agent.jsonl"), "agent");
        var memory = WriteFile(Path.Combine(Path.GetDirectoryName(source)!, "memory", "MEMORY.md"), "memory");
        var written = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, written);

        var result = await RelocateAsync();

        var target = TranscriptPath(NewDirectory);
        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.Moved, result.Status, result.Message);
        Assert.IsTrue(result.IsAtTarget);
        Assert.AreEqual(source, result.SourcePath);
        Assert.AreEqual(target, result.TargetPath);
        Assert.IsNull(result.BackupPath);
        Assert.IsNull(result.Message);
        Assert.AreEqual("line 1\nline 2\n", File.ReadAllText(target));
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(target));
        Assert.AreEqual("result", File.ReadAllText(Path.Combine(Path.GetDirectoryName(target)!, SessionId, "tool-results", "result.txt")));
        Assert.AreEqual("agent", File.ReadAllText(Path.Combine(Path.GetDirectoryName(target)!, SessionId, "subagents", "agent.jsonl")));
        Assert.IsFalse(File.Exists(source));
        Assert.IsFalse(Directory.Exists(companion));
        Assert.IsTrue(File.Exists(memory));
        Assert.IsFalse(File.Exists(target + ".tmp"));
    }

    [TestMethod]
    public async Task ProviderRuntime_MovesTheTranscriptOfASessionItsStateNames()
    {
        var source = WriteTranscript(OldDirectory, "conversation");
        await using var runtime = new ClaudeCodeModelProviderRuntime(new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude-code",
            GetEnvironmentVariable = name => name == ClaudeCodeProjectFolders.ConfigDirectoryVariable ? _root : null,
        });
        var state = new CodeAlta.Agent.Runtime.AgentSessionState { SessionId = "codealta-session", ProviderKey = "claude-code",
            ProtocolFamily = "claude-code", ProviderState = new ClaudeCodeProviderState(SessionId, 2, 2).ToJson(), UpdatedAt = DateTimeOffset.UtcNow };

        Assert.IsNull(await runtime.RelocateSessionAsync(state, OldDirectory, NewDirectory));
        Assert.AreEqual("conversation", File.ReadAllText(TranscriptPath(NewDirectory)));
        Assert.IsFalse(File.Exists(source));
        Assert.IsNull(await runtime.RelocateSessionAsync(state with { ProviderState = null }, NewDirectory, OldDirectory),
            "A session that has no conversation of the CLI has nothing to move.");
        Assert.IsTrue(File.Exists(TranscriptPath(NewDirectory)));
    }

    [TestMethod]
    public async Task ProviderRuntime_MovesTheTranscriptInTheConfigFolderOfTheProvider()
    {
        var source = WriteTranscript(OldDirectory, "conversation");
        await using var runtime = new ClaudeCodeModelProviderRuntime(new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude-business",
            ConfigDirectory = _root,
            // The folder of the provider wins over the one of the environment CodeAlta was started with.
            GetEnvironmentVariable = name => name == ClaudeCodeProjectFolders.ConfigDirectoryVariable ? Path.Combine(_root, "elsewhere") : null,
        });
        var state = new CodeAlta.Agent.Runtime.AgentSessionState { SessionId = "codealta-session", ProviderKey = "claude-business",
            ProtocolFamily = "claude-code", ProviderState = new ClaudeCodeProviderState(SessionId, 2, 2).ToJson(), UpdatedAt = DateTimeOffset.UtcNow };

        Assert.IsNull(await runtime.RelocateSessionAsync(state, OldDirectory, NewDirectory));
        Assert.AreEqual("conversation", File.ReadAllText(TranscriptPath(NewDirectory)));
        Assert.IsFalse(File.Exists(source));
    }

    [TestMethod]
    public void ExpandConfigDirectory_ExpandsHomeAndMakesThePathFull()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");

        Assert.IsNull(ClaudeCodeProjectFolders.ExpandConfigDirectory(" ", home));
        Assert.AreEqual(Path.Combine(home, ".claude-business"), ClaudeCodeProjectFolders.ExpandConfigDirectory("~/.claude-business", home));
        Assert.AreEqual(Path.Combine(home, ".claude-business"), ClaudeCodeProjectFolders.ExpandConfigDirectory(@"~\.claude-business\", home));
        Assert.AreEqual(home, ClaudeCodeProjectFolders.ExpandConfigDirectory("~", home));
        Assert.AreEqual(Path.Combine(home, "profiles", "work"), ClaudeCodeProjectFolders.ExpandConfigDirectory(Path.Combine(home, "profiles", "work"), null));
        Assert.ThrowsExactly<ArgumentException>(() => ClaudeCodeProjectFolders.ExpandConfigDirectory("~/x", null));
    }

    [TestMethod]
    public async Task Relocate_TakesTheOnlyCopyOfAnotherFolder()
    {
        var source = WriteTranscript(@"C:\elsewhere", "content");

        var result = await RelocateAsync();

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.Moved, result.Status, result.Message);
        Assert.AreEqual(source, result.SourcePath);
        Assert.AreEqual("content", File.ReadAllText(TranscriptPath(NewDirectory)));
        Assert.IsFalse(File.Exists(source));
    }

    [TestMethod]
    public async Task Relocate_MovesNothingWhenSeveralFoldersHaveIt()
    {
        var first = WriteTranscript(@"C:\one", "one");
        var second = WriteTranscript(@"C:\two", "two");

        var result = await RelocateAsync();

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.Ambiguous, result.Status);
        Assert.IsFalse(result.IsAtTarget);
        StringAssert.Contains(result.Message, first);
        StringAssert.Contains(result.Message, second);
        Assert.IsTrue(File.Exists(first));
        Assert.IsTrue(File.Exists(second));
        Assert.IsFalse(Directory.Exists(ClaudeCodeProjectFolders.GetProjectFolder(_root, NewDirectory)));
    }

    [TestMethod]
    public async Task Relocate_ReportsNothingWhenNoFolderHasIt()
    {
        var withoutProjects = await RelocateAsync();
        WriteTranscript(@"C:\one", "another conversation", sessionId: "other");
        var withOthers = await RelocateAsync();

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.NothingToMove, withoutProjects.Status);
        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.NothingToMove, withOthers.Status);
        Assert.IsFalse(withOthers.IsAtTarget);
    }

    [TestMethod]
    public async Task Relocate_DoesNothingForTheSameFolder()
    {
        var source = WriteTranscript(OldDirectory, "content");

        // "work\old" and "work-old" name the same folder.
        var result = await ClaudeCodeTranscriptRelocation.RelocateAsync(_root, SessionId, OldDirectory, @"C:\work-old", CancellationToken.None);

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.SameFolder, result.Status);
        Assert.IsTrue(result.IsAtTarget);
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task Relocate_IgnoresTheCaseOfAFolderOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Only Windows ignores the case of a folder name.");
        }

        var result = await ClaudeCodeTranscriptRelocation.RelocateAsync(_root, SessionId, @"C:\Work\Old", @"c:\work\old", CancellationToken.None);

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.SameFolder, result.Status);
    }

    [TestMethod]
    public async Task Relocate_RemovesTheSourceWhenTheTargetIsTheSame()
    {
        var source = WriteTranscript(OldDirectory, "content");
        WriteFile(Path.Combine(Path.GetDirectoryName(source)!, SessionId, "tool-results", "result.txt"), "result");
        var target = WriteTranscript(NewDirectory, "content");

        var result = await RelocateAsync();

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.AlreadyThere, result.Status, result.Message);
        Assert.IsNull(result.BackupPath);
        Assert.IsFalse(File.Exists(source));
        Assert.IsFalse(Directory.Exists(Path.Combine(Path.GetDirectoryName(source)!, SessionId)));
        Assert.AreEqual("content", File.ReadAllText(target));
        Assert.AreEqual("result", File.ReadAllText(Path.Combine(Path.GetDirectoryName(target)!, SessionId, "tool-results", "result.txt")));
    }

    [TestMethod]
    public async Task Relocate_KeepsALongerTargetAndSetsTheSourceAside()
    {
        var source = WriteTranscript(OldDirectory, "short");
        var target = WriteTranscript(NewDirectory, "the longer one");

        var result = await RelocateAsync();

        var backup = source + ".bak-20261010123456";
        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.AlreadyThere, result.Status);
        Assert.AreEqual(backup, result.BackupPath);
        Assert.IsNotNull(result.Message);
        Assert.AreEqual("the longer one", File.ReadAllText(target));
        Assert.AreEqual("short", File.ReadAllText(backup));
        Assert.IsFalse(File.Exists(source));
    }

    [TestMethod]
    public async Task Relocate_ReplacesAShorterTargetAndSetsItAside()
    {
        var source = WriteTranscript(OldDirectory, "the longer one");
        var target = WriteTranscript(NewDirectory, "short");
        var targetCompanion = Path.Combine(Path.GetDirectoryName(target)!, SessionId);
        WriteFile(Path.Combine(targetCompanion, "tool-results", "old.txt"), "old");
        WriteFile(Path.Combine(Path.GetDirectoryName(source)!, SessionId, "tool-results", "new.txt"), "new");

        var result = await RelocateAsync();

        var backup = target + ".bak-20261010123456";
        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.Moved, result.Status, result.Message);
        Assert.AreEqual(backup, result.BackupPath);
        Assert.AreEqual("the longer one", File.ReadAllText(target));
        Assert.AreEqual("short", File.ReadAllText(backup));
        Assert.IsTrue(File.Exists(Path.Combine(targetCompanion, "tool-results", "new.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(targetCompanion, "tool-results", "old.txt")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(targetCompanion + ".bak-20261010123456", "tool-results", "old.txt")));
        Assert.IsFalse(File.Exists(source));
    }

    [TestMethod]
    public async Task Relocate_KeepsTheSourceWhenItCannotBeRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Only Windows locks a file another stream opened.");
        }

        var source = WriteTranscript(OldDirectory, "content");
        ClaudeCodeTranscriptRelocationResult result;
        using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await RelocateAsync();
        }

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.Failed, result.Status);
        Assert.IsFalse(result.IsAtTarget);
        StringAssert.Contains(result.Message, SessionId);
        Assert.AreEqual("content", File.ReadAllText(source));
        Assert.IsFalse(Directory.Exists(ClaudeCodeProjectFolders.GetProjectFolder(_root, NewDirectory)));
    }

    [TestMethod]
    public async Task Relocate_RemovesTheCopyWhenTheSourceCannotBeDeleted()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Only Windows refuses to delete a file another stream opened.");
        }

        // The source can be read, so it is copied, but not deleted: two copies must not remain.
        var source = WriteTranscript(OldDirectory, "content");
        WriteFile(Path.Combine(Path.GetDirectoryName(source)!, SessionId, "tool-results", "result.txt"), "result");
        ClaudeCodeTranscriptRelocationResult result;
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await RelocateAsync();
        }

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.Failed, result.Status);
        Assert.AreEqual("content", File.ReadAllText(source));
        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(source)!, SessionId, "tool-results", "result.txt")));
        Assert.IsFalse(Directory.Exists(ClaudeCodeProjectFolders.GetProjectFolder(_root, NewDirectory)));
    }

    [TestMethod]
    public async Task Relocate_RestoresTheTargetWhenTheSourceCannotBeDeleted()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Only Windows refuses to delete a file another stream opened.");
        }

        var source = WriteTranscript(OldDirectory, "the longer one");
        var target = WriteTranscript(NewDirectory, "short");
        ClaudeCodeTranscriptRelocationResult result;
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await RelocateAsync();
        }

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.Failed, result.Status);
        Assert.AreEqual("the longer one", File.ReadAllText(source));
        Assert.AreEqual("short", File.ReadAllText(target));
        CollectionAssert.AreEqual(new[] { target }, Directory.GetFileSystemEntries(Path.GetDirectoryName(target)!));
    }

    [TestMethod]
    public async Task Relocate_LeavesTheTranscriptAlone_WhenTheTargetFolderIsALinkToTheSource()
    {
        // Users link the folders of the worktrees of one repository so that they share their conversations: a move
        // between them once took the transcript for a copy already at the target, and deleted the only one.
        var source = WriteTranscript(OldDirectory, "the only copy");
        LinkFolder(ClaudeCodeProjectFolders.GetProjectFolder(_root, NewDirectory), Path.GetDirectoryName(source)!);

        var result = await RelocateAsync();

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.SameFolder, result.Status, result.Message);
        Assert.IsTrue(result.IsAtTarget);
        Assert.AreEqual("the only copy", File.ReadAllText(source));
        Assert.AreEqual("the only copy", File.ReadAllText(TranscriptPath(NewDirectory)));
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(source)!).Length, "No copy, backup or probe is left beside it.");
    }

    [TestMethod]
    public async Task Relocate_TakesALinkedFolderForOneCopy_NotForSeveral()
    {
        var elsewhere = WriteTranscript(@"C:\elsewhere", "content");
        LinkFolder(ClaudeCodeProjectFolders.GetProjectFolder(_root, @"C:\elsewhere-link"), Path.GetDirectoryName(elsewhere)!);

        var result = await RelocateAsync();

        Assert.AreEqual(ClaudeCodeTranscriptRelocationStatus.Moved, result.Status, result.Message);
        Assert.AreEqual("content", File.ReadAllText(TranscriptPath(NewDirectory)));
        Assert.IsFalse(File.Exists(elsewhere));
    }

    [TestMethod]
    public void Locate_FindsTheTranscriptInTheFolderOfTheWorkingDirectory()
    {
        WriteTranscript(OldDirectory, "content");

        Assert.AreEqual(ClaudeCodeTranscriptPresence.Present, ClaudeCodeTranscriptRelocation.LocateTranscript(_root, OldDirectory, SessionId));
    }

    [TestMethod]
    public void Locate_FindsTheTranscriptInAnotherFolder_AsTheCliAcceptsIt()
    {
        WriteTranscript(@"C:\elsewhere", "content");
        Directory.CreateDirectory(ClaudeCodeProjectFolders.GetProjectFolder(_root, OldDirectory));

        Assert.AreEqual(ClaudeCodeTranscriptPresence.Present, ClaudeCodeTranscriptRelocation.LocateTranscript(_root, OldDirectory, SessionId));
    }

    [TestMethod]
    public void Locate_FindsTheTranscriptThroughALinkedFolder()
    {
        var elsewhere = WriteTranscript(@"C:\elsewhere", "content");
        LinkFolder(ClaudeCodeProjectFolders.GetProjectFolder(_root, OldDirectory), Path.GetDirectoryName(elsewhere)!);

        Assert.AreEqual(ClaudeCodeTranscriptPresence.Present, ClaudeCodeTranscriptRelocation.LocateTranscript(_root, OldDirectory, SessionId));
    }

    [TestMethod]
    public void Locate_ReportsATranscriptNoFolderHasAsMissing()
    {
        var withoutProjects = ClaudeCodeTranscriptRelocation.LocateTranscript(_root, OldDirectory, SessionId);
        WriteTranscript(OldDirectory, "another conversation", sessionId: "other");
        // Not a transcript: a copy a sync tool left beside one.
        WriteFile(Path.Combine(ClaudeCodeProjectFolders.GetProjectFolder(_root, @"C:\one"), SessionId + "_HOST_Conflict.jsonl"), "copy");

        Assert.AreEqual(ClaudeCodeTranscriptPresence.Missing, withoutProjects);
        Assert.AreEqual(ClaudeCodeTranscriptPresence.Missing, ClaudeCodeTranscriptRelocation.LocateTranscript(_root, OldDirectory, SessionId));
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow(" padded ")]
    public void Locate_DoesNotLookForAnIdentifierThatIsNotAFileName(string sessionId)
        => Assert.AreEqual(ClaudeCodeTranscriptPresence.Unknown, ClaudeCodeTranscriptRelocation.LocateTranscript(_root, OldDirectory, sessionId));

    // A junction on Windows, which needs no privilege and is what users make there; a symbolic link elsewhere.
    private static void LinkFolder(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            mklink.WaitForExit();
            if (mklink.ExitCode != 0 || !Directory.Exists(link)) Assert.Inconclusive($"mklink /J failed: {mklink.StandardError.ReadToEnd()}");
            return;
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"This system refuses symbolic links: {exception.Message}");
        }
    }

    private Task<ClaudeCodeTranscriptRelocationResult> RelocateAsync()
        => ClaudeCodeTranscriptRelocation.RelocateAsync(_root, SessionId, OldDirectory, NewDirectory, _time, CancellationToken.None);

    private string TranscriptPath(string workingDirectory, string sessionId = SessionId)
        => Path.Combine(ClaudeCodeProjectFolders.GetProjectFolder(_root, workingDirectory), sessionId + ".jsonl");

    private string WriteTranscript(string workingDirectory, string content, string sessionId = SessionId)
        => WriteFile(TranscriptPath(workingDirectory, sessionId), content);

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
