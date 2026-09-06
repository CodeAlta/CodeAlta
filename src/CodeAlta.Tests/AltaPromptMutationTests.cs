using System.Text;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AltaPromptMutationTests
{
    [TestMethod]
    [DataRow("global", false, 1200)]
    [DataRow("global", true, 1201)]
    [DataRow("project", false, 65001)]
    [DataRow("project", true, 12000)]
    [DataRow("project", false, 12001)]
    public async Task Edit_PreservesRawTextAndExistingBom(string scope, bool system, int codePage)
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor(scope, system);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        await File.WriteAllTextAsync(path, "---\r\nmode: invalid\r\n---\r\nold", encoding);
        const string replacement = "---\r\n# comment\r\nunknown: retained\r\nname: Repaired\r\n---\r\n\r\nnew\nlast";

        var args = new List<string> { "prompt", "edit", "sample", "--scope", scope, "--stdin" };
        if (system) args.Add("--system");
        var result = await fixture.InvokeAsync(args.ToArray(), replacement);

        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        var file = fixture.Codec.Load(path);
        Assert.IsTrue(file.HasByteOrderMark);
        Assert.AreEqual(codePage, file.Encoding.CodePage);
        Assert.AreEqual(replacement, file.Text);
        Assert.IsTrue(Record(result, "alta.prompt.edit").GetProperty("updated").GetBoolean());
    }

    [TestMethod]
    [DataRow("change")]
    [DataRow("delete")]
    [DataRow("create")]
    public async Task Edit_RejectsExternalChangeWhileReadingStdin(string change)
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor("global", false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (change != "create") await File.WriteAllTextAsync(path, "original");
        using var input = new ControlledReader();
        var pending = fixture.InvokeWithReaderAsync(["prompt", "edit", "sample", "--scope", "global", "--stdin"], input);
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (change == "delete") File.Delete(path);
        else await File.WriteAllTextAsync(path, "external");
        input.Content.SetResult("stale replacement");
        var result = await pending;

        Assert.AreNotEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        if (change == "delete") Assert.IsFalse(File.Exists(path));
        else Assert.AreEqual("external", await File.ReadAllTextAsync(path));
        Assert.IsFalse(result.Stdout.Contains("\"updated\":true", StringComparison.Ordinal));
        StringAssert.Contains(result.Stderr, "alta.error");
    }

    [TestMethod]
    [DataRow("global", false)]
    [DataRow("global", true)]
    [DataRow("project", false)]
    [DataRow("project", true)]
    public async Task CreateAndEdit_PreserveScopedRecordsAndSemanticMetadata(string scope, bool system)
    {
        using var fixture = new Fixture();
        var args = new List<string> { "prompt", "create", "sample", "--scope", scope, "--name", "Name: \"quoted\"\\line\nnext", "--description", "Desc # unknown\tend", "--system-prompt-id", "base.v2", "--stdin" };
        if (system) args.Add("--system");
        var created = await fixture.InvokeAsync(args.ToArray(), "  body\nnext  ");
        Assert.AreEqual(AltaExitCodes.Success, created.ExitCode, created.Stdout + created.Stderr);
        var record = Record(created, "alta.prompt.created");
        Assert.AreEqual("sample", record.GetProperty("id").GetString());
        Assert.AreEqual("sample", record.GetProperty("promptId").GetString());
        Assert.AreEqual(scope, record.GetProperty("scope").GetString());
        Assert.AreEqual(scope, record.GetProperty("source").GetString());
        Assert.AreEqual(system ? "system" : "user", record.GetProperty("promptKind").GetString());
        Assert.AreEqual(fixture.PathFor(scope, system), record.GetProperty("path").GetString());
        Assert.IsTrue(record.GetProperty("created").GetBoolean());
        Assert.AreEqual(1, record.GetProperty("version").GetInt32());
        Assert.IsFalse(string.IsNullOrWhiteSpace(record.GetProperty("correlationId").GetString()));
        var file = fixture.Codec.Load(fixture.PathFor(scope, system));
        var parsed = PromptFileFormat.Parse(system ? PromptResourceKind.System : PromptResourceKind.Agent, file.Text);
        Assert.AreEqual("Name: \"quoted\"\\line\nnext", parsed.Name);
        Assert.AreEqual("Desc # unknown\tend", parsed.Description);
        Assert.AreEqual("body\nnext", parsed.Body);
        if (!system) Assert.AreEqual("base.v2", parsed.SystemPromptName);
        Assert.IsFalse(file.HasByteOrderMark);
        var duplicate = await fixture.InvokeAsync(args.ToArray(), "replacement");
        Assert.AreEqual(AltaExitCodes.Usage, duplicate.ExitCode);
        AssertFailure(duplicate, "usage.promptExists");
        Assert.AreEqual(file.Revision, fixture.Codec.Load(fixture.PathFor(scope, system)).Revision);

        args = ["prompt", "edit", "sample", "--scope", scope, "--content", "# raw\r\n\nunknown: untouched\r"];
        if (system) args.Add("--system");
        var edited = await fixture.InvokeAsync(args.ToArray());
        Assert.AreEqual(AltaExitCodes.Success, edited.ExitCode, edited.Stdout + edited.Stderr);
        var editRecord = Record(edited, "alta.prompt.edit");
        Assert.AreEqual(scope, editRecord.GetProperty("source").GetString());
        Assert.AreEqual(scope, editRecord.GetProperty("scope").GetString());
        Assert.AreEqual("sample", editRecord.GetProperty("id").GetString());
        Assert.AreEqual("sample", editRecord.GetProperty("promptId").GetString());
        Assert.AreEqual(system ? "system" : "user", editRecord.GetProperty("promptKind").GetString());
        Assert.IsTrue(editRecord.GetProperty("exists").GetBoolean());
        Assert.IsTrue(editRecord.GetProperty("updated").GetBoolean());
        Assert.AreEqual("# raw\r\n\nunknown: untouched\r", await File.ReadAllTextAsync(fixture.PathFor(scope, system)));
    }

    [TestMethod]
    [DataRow("global", false)]
    [DataRow("global", true)]
    [DataRow("project", false)]
    [DataRow("project", true)]
    public async Task Edit_MissingAndNoContentLookup_DoNotCreateUntilReplacement(string scope, bool system)
    {
        using var fixture = new Fixture();
        var args = new List<string> { "prompt", "edit", "sample", "--scope", scope };
        if (system) args.Add("--system");
        var lookup = await fixture.InvokeAsync(args.ToArray());
        Assert.AreEqual(AltaExitCodes.Success, lookup.ExitCode, lookup.Stdout + lookup.Stderr);
        Assert.IsFalse(Record(lookup, "alta.prompt.edit").GetProperty("exists").GetBoolean());
        Assert.IsFalse(Record(lookup, "alta.prompt.edit").GetProperty("updated").GetBoolean());
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(fixture.PathFor(scope, system))));
        var edit = await fixture.InvokeAsync([.. args, "--stdin"], "");
        Assert.AreEqual(AltaExitCodes.Success, edit.ExitCode, edit.Stdout + edit.Stderr);
        Assert.IsTrue(Record(edit, "alta.prompt.edit").GetProperty("updated").GetBoolean());
        Assert.AreEqual(0L, new FileInfo(fixture.PathFor(scope, system)).Length);
        // Lookup neither decodes nor rewrites existing malformed bytes.
        await File.WriteAllBytesAsync(fixture.PathFor(scope, system), [0xff, 0xfe, 0x00]);
        lookup = await fixture.InvokeAsync(args.ToArray());
        Assert.AreEqual(AltaExitCodes.Success, lookup.ExitCode, lookup.Stdout + lookup.Stderr);
        Assert.IsTrue(Record(lookup, "alta.prompt.edit").GetProperty("exists").GetBoolean());
        CollectionAssert.AreEqual(new byte[] { 0xff, 0xfe, 0x00 }, await File.ReadAllBytesAsync(fixture.PathFor(scope, system)));
    }

    [TestMethod]
    [DataRow("create", "../escape", "global")]
    [DataRow("edit", "../escape", "global")]
    [DataRow("edit", "a\\b", "global")]
    [DataRow("create", "CON", "global")]
    [DataRow("edit", "sample.prompt.md", "global")]
    [DataRow("create", "sample.system-prompt.md", "global")]
    [DataRow("create", "sample", "builtin")]
    [DataRow("edit", "sample", "builtin")]
    [DataRow("create", "sample", "all")]
    [DataRow("edit", "sample", "all")]
    public async Task Mutations_RejectUnsafeIdsAndScopes(string command, string id, string scope)
    {
        using var fixture = new Fixture();
        var result = await fixture.InvokeAsync(["prompt", command, id, "--scope", scope, "--content", "body"]);
        Assert.AreEqual(AltaExitCodes.Usage, result.ExitCode, result.Stdout + result.Stderr);
        AssertFailure(result, "usage.");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.GlobalRoot, "prompts")));
    }

    [TestMethod]
    [DataRow("create")]
    [DataRow("edit")]
    public async Task Mutations_RejectMissingProjectAndConflictingContent(string command)
    {
        using var fixture = new Fixture();
        var missing = await fixture.InvokeAsync(["prompt", command, "sample", "--scope", "project", "--content", "body"], cwd: Path.Combine(fixture.Cwd, "missing"));
        Assert.AreEqual(AltaExitCodes.Usage, missing.ExitCode, missing.Stdout + missing.Stderr);
        AssertFailure(missing, "usage.missingProject");
        var conflict = await fixture.InvokeAsync(["prompt", command, "sample", "--scope", "global", "--content", " ", "--stdin"], "stdin");
        Assert.AreEqual(AltaExitCodes.Usage, conflict.ExitCode, conflict.Stdout + conflict.Stderr);
        AssertFailure(conflict, "usage.contentConflict");
    }

    [TestMethod]
    public async Task Create_DefaultsAndPortableIdsAndInvalidSystemReference()
    {
        using var fixture = new Fixture();
        var result = await fixture.InvokeAsync(["prompt", "create", "réview.v2_test-1", "--scope", "global", "--content", "body"]);
        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        var record = Record(result, "alta.prompt.created");
        Assert.AreEqual("réview.v2_test-1", record.GetProperty("name").GetString());
        Assert.AreEqual("default", record.GetProperty("systemPromptId").GetString());
        var invalid = await fixture.InvokeAsync(["prompt", "create", "sample", "--scope", "global", "--system-prompt-id", "../escape", "--content", "body"]);
        Assert.AreEqual(AltaExitCodes.Usage, invalid.ExitCode, invalid.Stdout + invalid.Stderr);
        Assert.IsFalse(File.Exists(fixture.PathFor("global", false)));
        var empty = await fixture.InvokeAsync(["prompt", "create", "sample", "--scope", "global", "--stdin"], "  ");
        AssertFailure(empty, "usage.missingContent");
    }

    [TestMethod]
    public async Task Create_RejectsExternalCreationWhileReadingStdin()
    {
        using var fixture = new Fixture();
        using var input = new ControlledReader();
        var pending = fixture.InvokeWithReaderAsync(["prompt", "create", "sample", "--scope", "global", "--stdin"], input);
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var path = fixture.PathFor("global", false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "external");
        input.Content.SetResult("new body");
        AssertFailure(await pending, "usage.promptExists");
        Assert.AreEqual("external", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    [DataRow("decode")]
    [DataRow("read")]
    [DataRow("write")]
    [DataRow("directory")]
    public async Task Edit_StorageFailuresNeverBecomeEmptyBaselinesOrSuccess(string failure)
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor("global", false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] original = failure == "decode" ? [0xff, 0xfe, 0x00] : Encoding.UTF8.GetBytes("original");
        if (failure == "directory") Directory.CreateDirectory(path);
        else await File.WriteAllBytesAsync(path, original);
        FileStream? locked = failure == "read" ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        if (failure == "write") File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = await fixture.InvokeAsync(["prompt", "edit", "sample", "--scope", "global", "--stdin"], "replacement");
            AssertFailure(result, "prompt.storageFailed");
        }
        finally
        {
            locked?.Dispose();
            if (failure == "write") File.SetAttributes(path, FileAttributes.Normal);
        }
        if (failure == "directory") Assert.IsTrue(Directory.Exists(path));
        else CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task Create_StorageFailureDoesNotReportCreated()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.GlobalRoot, "prompts"), "not a directory");
        var result = await fixture.InvokeAsync(["prompt", "create", "sample", "--scope", "global", "--content", "body"]);
        AssertFailure(result, "prompt.storageFailed");
    }

    [TestMethod]
    public async Task Edit_InvalidReplacementUnicodeDoesNotCommit()
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor("global", false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "original");
        var result = await fixture.InvokeAsync(["prompt", "edit", "sample", "--scope", "global", "--stdin"], "invalid\ud800");
        AssertFailure(result, "prompt.storageFailed");
        Assert.AreEqual("original", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" \t\r\n ")]
    public async Task Edit_ExplicitContentIsLiteralEvenWhenEmptyOrWhitespace(string content)
    {
        using var fixture = new Fixture();
        var result = await fixture.InvokeAsync(["prompt", "edit", "sample", "--scope", "global", "--content", content]);
        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        Assert.IsTrue(Record(result, "alta.prompt.edit").GetProperty("updated").GetBoolean());
        Assert.AreEqual(content, await File.ReadAllTextAsync(fixture.PathFor("global", false)));
    }

    [TestMethod]
    [DataRow("create")]
    [DataRow("edit")]
    public async Task Mutations_CancelDuringStdinWithoutCommit(string command)
    {
        using var fixture = new Fixture();
        using var input = new ControlledReader();
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.InvokeWithReaderAsync(["prompt", command, "sample", "--scope", "global", "--stdin"], input, cancellation.Token);
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var result = await pending;
        Assert.AreEqual(AltaExitCodes.TimeoutOrCancellation, result.ExitCode, result.Stdout + result.Stderr);
        AssertFailure(result, "runtime.cancelled");
        Assert.IsFalse(File.Exists(fixture.PathFor("global", false)));
    }

    [TestMethod]
    [DataRow("create")]
    [DataRow("edit")]
    public async Task Mutations_HonorPrecancelledInvocation(string command)
    {
        using var fixture = new Fixture();
        var result = await fixture.InvokeAsync(["prompt", command, "sample", "--scope", "global", "--content", "body"], cancellationToken: new CancellationToken(true));
        Assert.AreEqual(AltaExitCodes.TimeoutOrCancellation, result.ExitCode, result.Stdout + result.Stderr);
        AssertFailure(result, "runtime.cancelled");
        Assert.IsFalse(File.Exists(fixture.PathFor("global", false)));
    }

    [TestMethod]
    [DataRow("change")]
    [DataRow("delete")]
    [DataRow("create")]
    [DataRow("cancel")]
    public async Task Edit_RetainsBaselineWhenSharedCodecGateIsBusy(string change)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var path = fixture.PathFor("global", false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (change != "create") await File.WriteAllTextAsync(path, "original");
        // Test-only access to the existing codec gate, avoiding new production timing hooks.
        var gate = (SemaphoreSlim)typeof(TextFileCodec).GetField("_saveGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Codec)!;
        await gate.WaitAsync();
        using var input = new ControlledReader(inlineCompletion: true);
        var pending = fixture.InvokeWithReaderAsync(["prompt", "edit", "sample", "--scope", "global", "--stdin"], input, cancellation.Token);
        try
        {
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Keep the shared save gate held as the handler leaves controlled stdin.
            input.Content.SetResult("replacement");
            Assert.IsFalse(pending.IsCompleted);
            if (change == "cancel") cancellation.Cancel();
            else if (change == "delete") File.Delete(path);
            else await File.WriteAllTextAsync(path, "external");
        }
        finally { gate.Release(); }
        var result = await pending;
        AssertFailure(result, change == "cancel" ? "runtime.cancelled" : "prompt.conflict");
        if (change == "delete") Assert.IsFalse(File.Exists(path));
        else Assert.AreEqual(change == "cancel" ? "original" : "external", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    [DataRow("create")]
    [DataRow("edit")]
    public async Task Mutations_CancellationAfterCommitStillReportsSuccess(string command)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        using var stdout = new CancelOnWriteWriter(cancellation);
        var result = await fixture.InvokeWithReaderAsync(["prompt", command, "sample", "--scope", "global", "--stdin"], new StringReader("body"), cancellation.Token, stdout);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        Assert.IsTrue(File.Exists(fixture.PathFor("global", false)));
        Assert.IsTrue(Record(result, command == "create" ? "alta.prompt.created" : "alta.prompt.edit").GetProperty(command == "create" ? "created" : "updated").GetBoolean());
    }

    [TestMethod]
    [DataRow("file")]
    [DataRow("directory")]
    [DataRow("root")]
    public async Task Mutations_RejectLinkedPathsWithoutTouchingTargets(string linkKind)
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Cwd, "owned-link-target");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "target.md");
        await File.WriteAllTextAsync(target, "outside");
        var path = fixture.PathFor("global", false);
        var link = linkKind switch { "file" => path, "directory" => Path.GetDirectoryName(path)!, _ => Path.Combine(fixture.GlobalRoot, "prompts") };
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            if (linkKind == "file") File.CreateSymbolicLink(link, target);
            else Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Creating task-owned links requires platform permission.");
        }
        try
        {
            foreach (var command in new[] { "create", "edit" })
            {
                var result = await fixture.InvokeAsync(["prompt", command, "sample", "--scope", "global", "--content", "body"]);
                AssertFailure(result, "prompt.storageFailed");
            }
            var lookup = await fixture.InvokeAsync(["prompt", "edit", "sample", "--scope", "global"]);
            AssertFailure(lookup, "prompt.storageFailed");
            Assert.AreEqual("outside", await File.ReadAllTextAsync(target));
            Assert.AreEqual(1, Directory.GetFiles(outside).Length);
        }
        finally
        {
            if (linkKind == "file") File.Delete(link);
            else Directory.Delete(link);
        }
    }

    private static void AssertFailure(AltaCommandResult result, string code)
    {
        Assert.AreNotEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        StringAssert.Contains(result.Stdout + result.Stderr, "alta.error");
        StringAssert.Contains(result.Stdout + result.Stderr, code);
        Assert.IsFalse(result.Stdout.Contains("\"type\":\"alta.prompt.created\"", StringComparison.Ordinal));
        Assert.IsFalse(result.Stdout.Contains("\"type\":\"alta.prompt.edit\"", StringComparison.Ordinal));
    }

    private static JsonElement Record(AltaCommandResult result, string type)
        => result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
            .Single(record => record.GetProperty("type").GetString() == type);

    // Only built-in command registration, CatalogOptions, and the shared codec. No app,
    // provider/runtime/project catalog, plugin contributor or discovery service is constructed.
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-prompt-mutations-" + Guid.NewGuid().ToString("N"));
        private readonly AltaCommandRegistry _registry = new([new BuiltInAltaCommandContributor()]);
        private readonly AltaServiceCollection _services;
        private readonly AltaCommandDispatcher _dispatcher;

        public Fixture()
        {
            GlobalRoot = Path.Combine(_root, "global");
            Cwd = Path.Combine(_root, "project");
            Directory.CreateDirectory(GlobalRoot);
            Directory.CreateDirectory(Cwd);
            _services = new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = GlobalRoot }).Add(Codec);
            _dispatcher = new AltaCommandDispatcher(_registry, _services);
        }

        public TextFileCodec Codec { get; } = new();
        public string GlobalRoot { get; }
        public string Cwd { get; }
        public string PathFor(string scope, bool system)
            => Path.Combine(scope == "project" ? Path.Combine(Cwd, ".alta") : GlobalRoot, "prompts", system ? "system" : "agents", system ? "sample.system-prompt.md" : "sample.prompt.md");

        public ValueTask<AltaCommandResult> InvokeAsync(string[] args, string? stdin = null, CancellationToken cancellationToken = default, string? cwd = null)
            => _dispatcher.InvokeAsync(args, stdin, AltaCallerIdentity.Cli, cwd ?? Cwd, cancellationToken: cancellationToken);

        public ValueTask<AltaCommandResult> InvokeWithReaderAsync(string[] args, TextReader stdin, CancellationToken cancellationToken = default, TextWriter? stdout = null)
            => _registry.InvokeAsync(args, new AltaCommandContext
            {
                Caller = AltaCallerIdentity.Cli,
                Services = _services,
                Stdin = stdin,
                Stdout = stdout ?? new StringWriter(),
                Stderr = new StringWriter(),
                Cwd = Cwd,
                CorrelationId = "prompt-mutation-test",
                CancellationToken = cancellationToken,
            });

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class ControlledReader(bool inlineCompletion = false) : TextReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Content { get; } = new(inlineCompletion ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<string> ReadToEndAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return await Content.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CancelOnWriteWriter(CancellationTokenSource cancellation) : StringWriter
    {
        public override void Write(string? value)
        {
            base.Write(value);
            cancellation.Cancel();
        }
    }
}
