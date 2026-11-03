using System.ComponentModel;
using System.Diagnostics;

using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A session given to another machine (fleet M2): the source freezes it and makes its bundle, the target prepares a
/// worktree of the same repository and installs the session with the same id once the move is committed, and the source
/// keeps a read-only copy no writer of its sessions folder takes back. Both machines are profiles of this computer here:
/// the network that carries the bundle is not part of it.
/// </summary>
[TestClass]
public sealed class SessionBundleMoveTests
{
    [TestMethod]
    public async Task ASessionOfAnApiProvider_MovesWithItsUnpushedCommitsAndItsDirtyTree_AndSecretsStayBehind()
    {
        using var machines = Machines.Create();
        var sourceProvider = new RecordingProvider();
        var targetProvider = new RecordingProvider();
        string sessionId, moveId = "move-" + Guid.NewGuid().ToString("N");
        SessionMoveStepResult offered;
        await using (var source = await machines.CreateSourceHostAsync(sourceProvider))
        {
            sessionId = (await source.Commands.CreateDraftSessionAsync(source.CurrentProject, sourceProvider.Descriptor, "Parser work")).SessionId;
            await SendAsync(source, sessionId, "Let us plan.");
            machines.Git(machines.SourceProject, "checkout", "-q", "-b", "feature/parser");
            machines.Write(machines.SourceProject, "src/parser.txt", "unpushed\n");
            machines.Commit(machines.SourceProject, "Parser");
            machines.Write(machines.SourceProject, "readme.md", "work in progress\n");
            machines.Write(machines.SourceProject, "notes.md", "untracked\n");
            machines.Write(machines.SourceProject, ".env", "TOKEN=secret\n");

            var moves = new SessionMoveService(source, "Windows PC") { DeviceIdSource = static () => "pcdevice" };
            offered = await moves.OfferAsync(sessionId, moveId, "Mac mini");
            Assert.AreEqual(SessionMoveState.Offered, offered.Status, string.Join(" ", offered.Warnings));
            Assert.AreEqual(SessionRelinquishment.Frozen, source.RuntimeService.Relinquishments.Read(sessionId)!.State);
            var refused = await TrySendAsync(source, sessionId, "While it moves.");
            Assert.AreEqual((OwnedSessionCommandOutcome.Failed, SessionRuntimeService.SessionInUseCode), (refused.Outcome, refused.Code), "A frozen session is not written.");
        }

        var manifest = (await SessionBundle.ReadManifestAsync(offered.Transaction!.BundleFolder!, CancellationToken.None))!;
        Assert.AreEqual(sessionId, manifest.SessionId);
        Assert.AreEqual("feature/parser", manifest.Repository.Branch);
        Assert.AreEqual(1, manifest.Repository.Payload.UnpushedCommits);
        Assert.IsTrue(manifest.Repository.Dirty);
        Assert.AreEqual(machines.SourceProject, manifest.WorkingFolder);
        Assert.AreEqual(("Windows PC", "pcdevice"), (manifest.FromMachine, manifest.FromDeviceId), "The source is named by its device id too: a rename does not change it.");
        Assert.AreEqual(sourceProvider.Descriptor.ProviderId.Value, manifest.ProviderKey);
        Assert.IsNull(manifest.ProviderSession, "An API provider continues from the journal: nothing of its own travels.");
        CollectionAssert.Contains(manifest.Repository.Payload.LeftBehind.ToList(), new SessionMoveLeftBehind(".env", SessionMoveLeftBehind.Secret));

        await using (var target = await machines.CreateTargetHostAsync(targetProvider))
        {
            var moves = new SessionMoveService(target, "Mac mini") { DeviceIdSource = static () => "macdevice" };
            var received = await machines.TransferAsync(offered.Transaction!.BundleFolder!, moves.GetBundleFolder(moveId, SessionMoveRole.Target));
            var prepared = await moves.PrepareAsync(received, CancellationToken.None);
            Assert.AreEqual(SessionMoveState.Prepared, prepared.Status, string.Join(" ", prepared.Warnings));
            StringAssert.Contains(string.Join(" ", prepared.Warnings), ".env");
            var arbiter = new LocalSessionMoveArbiter(moves.Store);

            var committed = await moves.CommitAsync(moveId, arbiter, CancellationToken.None);

            Assert.AreEqual(SessionMoveState.Finished, committed.Status, string.Join(" ", committed.Warnings));
            Assert.AreEqual(1, committed.Transaction!.Epoch);
            var folder = committed.Transaction.WorkFolder!;
            Assert.AreEqual(folder, await target.RuntimeService.GetSessionWorktreeAsync(sessionId));
            Assert.AreEqual("feature/parser", machines.Git(folder, "branch", "--show-current").Trim());
            Assert.AreEqual("unpushed\n", File.ReadAllText(Path.Combine(folder, "src", "parser.txt")));
            Assert.AreEqual("work in progress\n", File.ReadAllText(Path.Combine(folder, "readme.md")));
            Assert.AreEqual("untracked\n", File.ReadAllText(Path.Combine(folder, "notes.md")));
            Assert.IsFalse(File.Exists(Path.Combine(folder, ".env")), "A secret stays on the source.");

            await SendAsync(target, sessionId, "Go on.");
            Assert.AreEqual(folder, targetProvider.LastWorkingDirectory);
            Assert.AreEqual(2, targetProvider.Conversations.Last(), "The conversation goes on from the journal.");
            StringAssert.Contains(targetProvider.LastUserText, "this session moved from Windows PC, `" + machines.SourceProject + "`, to Mac mini, `" + folder + "`");
            StringAssert.Contains(targetProvider.LastUserText, "Go on.");
            var journal = File.ReadAllText(Directory.EnumerateFiles(machines.TargetGlobal, sessionId + ".jsonl", SearchOption.AllDirectories).Single());
            StringAssert.Contains(journal, SessionMovedRecord.EventType);
            StringAssert.Contains(journal, "This session moved from Windows PC");
            // The statistics tell the machines that wrote the parts of the journal apart by these ids, whatever the machines are called later.
            StringAssert.Matches(journal, new System.Text.RegularExpressions.Regex("\"from\"\\s*:\\s*\"Windows PC\"\\s*,\\s*\"to\"\\s*:\\s*\"Mac mini\"\\s*,\\s*\"fromDevice\"\\s*:\\s*\"pcdevice\"\\s*,\\s*\"toDevice\"\\s*:\\s*\"macdevice\""));
        }

        // The source learns the outcome: it keeps its copy read-only.
        await using (var source = await machines.CreateSourceHostAsync(sourceProvider))
        {
            var moves = new SessionMoveService(source, "Windows PC");
            var completed = await moves.CompleteSourceAsync(moveId, new LocalSessionMoveArbiter(new SessionMoveTransactionStore(machines.TargetOptions)), CancellationToken.None);
            Assert.AreEqual(SessionMoveState.Finished, completed.Status);
            var record = source.RuntimeService.Relinquishments.Read(sessionId)!;
            Assert.IsTrue(record.IsRelinquished);
            Assert.AreEqual(("Mac mini", 1L), (record.ToMachine, record.Epoch));
            var refused = await TrySendAsync(source, sessionId, "Back here?");
            Assert.AreEqual(SessionRuntimeService.SessionInUseCode, refused.Code);
        }
    }

    [TestMethod]
    public async Task ASessionOfClaudeCode_CarriesItsTranscript_FromACleanPushedBranch()
    {
        using var machines = Machines.Create();
        var sourceProvider = new RecordingProvider { TranscriptRoot = Path.Combine(machines.Root, "source-claude") };
        var targetProvider = new RecordingProvider { TranscriptRoot = Path.Combine(machines.Root, "target-claude") };
        string sessionId, moveId = "move-" + Guid.NewGuid().ToString("N");
        string bundle;
        await using (var source = await machines.CreateSourceHostAsync(sourceProvider))
        {
            sessionId = (await source.Commands.CreateDraftSessionAsync(source.CurrentProject, sourceProvider.Descriptor, null)).SessionId;
            await SendAsync(source, sessionId, "Hello.");
            sourceProvider.WriteTranscript(machines.SourceProject, "the conversation of the CLI");

            var offered = await new SessionMoveService(source, "Laptop").OfferAsync(sessionId, moveId, "Server");
            Assert.AreEqual(SessionMoveState.Offered, offered.Status, string.Join(" ", offered.Warnings));
            bundle = offered.Transaction!.BundleFolder!;
        }

        var manifest = (await SessionBundle.ReadManifestAsync(bundle, CancellationToken.None))!;
        Assert.IsTrue(manifest.Repository.Payload.IsEmpty, "A pushed, clean branch ships no git payload.");
        Assert.IsFalse(manifest.Repository.Dirty);
        Assert.AreEqual(new SessionMoveProviderSession(sourceProvider.Descriptor.ProviderId.Value, SessionMoveManifest.ProviderFolder), manifest.ProviderSession);

        await using var target = await machines.CreateTargetHostAsync(targetProvider);
        var moves = new SessionMoveService(target, "Server");
        var received = await machines.TransferAsync(bundle, moves.GetBundleFolder(moveId, SessionMoveRole.Target));
        Assert.AreEqual(SessionMoveState.Prepared, (await moves.PrepareAsync(received, CancellationToken.None)).Status);
        var committed = await moves.CommitAsync(moveId, new LocalSessionMoveArbiter(moves.Store), CancellationToken.None);

        Assert.AreEqual(SessionMoveState.Finished, committed.Status, string.Join(" ", committed.Warnings));
        var folder = committed.Transaction!.WorkFolder!;
        Assert.AreEqual("the conversation of the CLI", targetProvider.ReadTranscript(folder), "The transcript is placed under the new working folder.");
        Assert.AreEqual("main-moved", committed.Transaction.Branch, "main is the branch of the target's own checkout.");
        await SendAsync(target, sessionId, "Continue.");
        Assert.AreEqual(folder, targetProvider.LastWorkingDirectory);
    }

    [TestMethod]
    public async Task ASessionAtWork_IsNotOffered_AndStaysWritable()
    {
        using var machines = Machines.Create();
        var provider = new RecordingProvider();
        await using var source = await machines.CreateSourceHostAsync(provider);
        var sessionId = (await source.Commands.CreateDraftSessionAsync(source.CurrentProject, provider.Descriptor, null)).SessionId;
        var moves = new SessionMoveService(source, "Windows PC");
        var gate = provider.Block();
        var receipt = source.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "A long turn.")).Receipt!;
        await gate.Started.WaitAsync(TestDeadlines.Hang);

        var refused = await moves.OfferAsync(sessionId, "move-busy", "Mac mini");

        Assert.AreEqual("busy", refused.Status);
        Assert.IsNull(source.RuntimeService.Relinquishments.Read(sessionId));
        Assert.IsNull(moves.Store.Read("move-busy", SessionMoveRole.Source));
        Assert.IsFalse(Directory.Exists(moves.GetBundleFolder("move-busy", SessionMoveRole.Source)));
        gate.Release();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await receipt.Completion.WaitAsync(TestDeadlines.Hang)).Outcome);

        // A prompt queued for later keeps it too: it would run on neither machine.
        var session = (await source.RuntimeService.ResolveOwnedSessionAsync(sessionId, CancellationToken.None))!;
        await source.RuntimeService.WhenSettledAsync(sessionId).WaitAsync(TestDeadlines.Hang);
        await source.RuntimeService.QueuePromptAsync(session, "Later.", "send", submittedBy: null);
        var queued = await moves.OfferAsync(sessionId, "move-queued", "Mac mini");
        Assert.AreNotEqual(SessionMoveState.Offered, queued.Status);
        Assert.IsNull(source.RuntimeService.Relinquishments.Read(sessionId));
    }

    [TestMethod]
    public async Task ARelinquishedSession_IsRefusedByASecondWriterOfTheSessionsFolder()
    {
        using var machines = Machines.Create();
        var provider = new RecordingProvider();
        string sessionId;
        await using (var source = await machines.CreateSourceHostAsync(provider))
        {
            sessionId = (await source.Commands.CreateDraftSessionAsync(source.CurrentProject, provider.Descriptor, null)).SessionId;
            await SendAsync(source, sessionId, "One.");
            var offered = await new SessionMoveService(source, "Windows PC").OfferAsync(sessionId, "move-gone", "Mac mini");
            Assert.AreEqual(SessionMoveState.Offered, offered.Status, string.Join(" ", offered.Warnings));
            source.RuntimeService.RelinquishMovedSession(sessionId, "move-gone", "Mac mini", 1);
        }

        // The command line, on the same sessions folder, with a database of its own.
        await using var cli = await machines.CreateSourceHostAsync(provider, SessionLeaseOwner.CliKind, readOnlyGlobalState: true);
        var refused = await TrySendAsync(cli, sessionId, "Resume here by mistake.");

        Assert.AreEqual((OwnedSessionCommandOutcome.Failed, SessionRuntimeService.SessionInUseCode), (refused.Outcome, refused.Code));
        Assert.AreEqual(1, provider.Conversations.Count, "No turn ran after the move.");
        Assert.AreEqual("relinquished", (await new SessionMoveService(cli, "Windows PC").OfferAsync(sessionId, "move-again", "Laptop")).Status);
    }

    [TestMethod]
    public async Task ACrashBetweenPreparedAndCommitted_IsUndoneOnBothSides()
    {
        using var machines = Machines.Create();
        var provider = new RecordingProvider();
        string sessionId, worktree;
        const string moveId = "move-crash";
        await using (var source = await machines.CreateSourceHostAsync(provider))
        {
            sessionId = (await source.Commands.CreateDraftSessionAsync(source.CurrentProject, provider.Descriptor, null)).SessionId;
            await SendAsync(source, sessionId, "One.");
            machines.Write(machines.SourceProject, "readme.md", "dirty\n");
            var offered = await new SessionMoveService(source, "Windows PC").OfferAsync(sessionId, moveId, "Mac mini");
            await using var target = await machines.CreateTargetHostAsync(provider);
            var moves = new SessionMoveService(target, "Mac mini");
            var received = await machines.TransferAsync(offered.Transaction!.BundleFolder!, moves.GetBundleFolder(moveId, SessionMoveRole.Target));
            var prepared = await moves.PrepareAsync(received, CancellationToken.None);
            Assert.AreEqual(SessionMoveState.Prepared, prepared.Status, string.Join(" ", prepared.Warnings));
            worktree = prepared.Transaction!.WorktreeRoot!;
            Assert.IsTrue(Directory.Exists(worktree));
            // Both machines stop here: the target never asked for the commit.
        }

        await using (var target = await machines.CreateTargetHostAsync(provider))
        {
            var moves = new SessionMoveService(target, "Mac mini");
            var recovered = (await moves.RecoverAsync(new LocalSessionMoveArbiter(moves.Store), CancellationToken.None)).Single();
            Assert.AreEqual(SessionMoveState.Aborted, recovered.Status);
            Assert.IsFalse(Directory.Exists(worktree), "What the target prepared is removed.");
            Assert.IsNull(await target.RuntimeService.ResolveOwnedSessionAsync(sessionId, CancellationToken.None), "The session never ran on the target.");
            Assert.AreEqual(SessionMoveState.Aborted,
                (await moves.CommitAsync(moveId, new LocalSessionMoveArbiter(moves.Store), CancellationToken.None)).Status, "A late commit is refused.");
        }

        await using (var source = await machines.CreateSourceHostAsync(provider))
        {
            var moves = new SessionMoveService(source, "Windows PC");
            var recovered = (await moves.RecoverAsync(new LocalSessionMoveArbiter(new SessionMoveTransactionStore(machines.TargetOptions)), CancellationToken.None)).Single();
            Assert.AreEqual(SessionMoveState.Aborted, recovered.Status);
            Assert.IsNull(source.RuntimeService.Relinquishments.Read(sessionId), "The source unfreezes the session.");
            await SendAsync(source, sessionId, "Still here.");
            Assert.AreEqual(machines.SourceProject, provider.LastWorkingDirectory);
        }
    }

    [TestMethod]
    public async Task ACrashBetweenTheFreezeAndTheOfferedRecord_IsUndoneWhenTheSourceStarts()
    {
        using var machines = Machines.Create();
        var provider = new RecordingProvider();
        string sessionId, bundle;
        const string moveId = "move-export-crash";
        await using (var source = await machines.CreateSourceHostAsync(provider))
        {
            sessionId = (await source.Commands.CreateDraftSessionAsync(source.CurrentProject, provider.Descriptor, null)).SessionId;
            await SendAsync(source, sessionId, "One.");
            // The source stops once the session is frozen and its bundle written, before the move is recorded as offered.
            var moves = new SessionMoveService(source, "Windows PC") { AfterExportForTesting = static _ => throw new SimulatedCrashException() };
            await Assert.ThrowsExactlyAsync<SimulatedCrashException>(() => moves.OfferAsync(sessionId, moveId, "Mac mini"));
            bundle = moves.GetBundleFolder(moveId, SessionMoveRole.Source);
            Assert.AreEqual(SessionRelinquishment.Frozen, source.RuntimeService.Relinquishments.Read(sessionId)!.State);
            Assert.IsTrue(Directory.Exists(bundle));
        }

        await using (var source = await machines.CreateSourceHostAsync(provider))
        {
            var moves = new SessionMoveService(source, "Windows PC");
            var recovered = (await moves.RecoverExportsAsync(CancellationToken.None)).Single();

            Assert.AreEqual(SessionMoveState.Aborted, recovered.Status);
            Assert.AreEqual(SessionMoveState.Aborted, moves.Store.Read(moveId, SessionMoveRole.Source)!.State);
            Assert.IsNull(source.RuntimeService.Relinquishments.Read(sessionId), "Nothing was offered to any machine: the session is written here again.");
            Assert.IsFalse(Directory.Exists(bundle), "The copy of the session goes.");
            await SendAsync(source, sessionId, "Still here.");
        }
    }

    [TestMethod]
    public async Task AFreezeNoMoveRecords_IsUndoneWhenTheSourceStarts_AndAnOfferedOneIsLeftToItsArbiter()
    {
        using var machines = Machines.Create();
        var provider = new RecordingProvider();
        await using var source = await machines.CreateSourceHostAsync(provider);
        var orphan = (await source.Commands.CreateDraftSessionAsync(source.CurrentProject, provider.Descriptor, null)).SessionId;
        var offered = (await source.Commands.CreateDraftSessionAsync(source.CurrentProject, provider.Descriptor, null)).SessionId;
        await SendAsync(source, orphan, "One.");
        await SendAsync(source, offered, "One.");
        var moves = new SessionMoveService(source, "Windows PC");
        Assert.AreEqual(SessionMoveState.Offered, (await moves.OfferAsync(offered, "move-offered", "Mac mini")).Status);
        // A freeze whose move left no record at all (a source that stopped before it recorded anything).
        Assert.IsTrue(source.RuntimeService.Relinquishments.Freeze(orphan, "move-orphan", "Mac mini"));

        var recovered = await moves.RecoverExportsAsync(CancellationToken.None);

        Assert.AreEqual(SessionMoveState.Aborted, recovered.Single().Status);
        Assert.IsNull(source.RuntimeService.Relinquishments.Read(orphan));
        Assert.AreEqual(SessionRelinquishment.Frozen, source.RuntimeService.Relinquishments.Read(offered)!.State,
            "An offered move may have been committed by its target: only its arbiter undoes it.");
    }

    [TestMethod]
    public async Task ACrashAfterTheArbiterCommitted_IsFinishedOnBothSides()
    {
        using var machines = Machines.Create();
        var provider = new RecordingProvider();
        string sessionId;
        const string moveId = "move-committed";
        await using (var source = await machines.CreateSourceHostAsync(provider))
        {
            sessionId = (await source.Commands.CreateDraftSessionAsync(source.CurrentProject, provider.Descriptor, null)).SessionId;
            await SendAsync(source, sessionId, "One.");
            var offered = await new SessionMoveService(source, "Windows PC").OfferAsync(sessionId, moveId, "Mac mini");
            await using var target = await machines.CreateTargetHostAsync(provider);
            var moves = new SessionMoveService(target, "Mac mini");
            var received = await machines.TransferAsync(offered.Transaction!.BundleFolder!, moves.GetBundleFolder(moveId, SessionMoveRole.Target));
            Assert.AreEqual(SessionMoveState.Prepared, (await moves.PrepareAsync(received, CancellationToken.None)).Status);
            // The arbiter records the commit, then the target stops before it records or installs anything.
            Assert.AreEqual(SessionMoveState.Committed, (await new LocalSessionMoveArbiter(moves.Store).CommitAsync(moveId, 1, CancellationToken.None)).Outcome);
        }

        // The source asks first: a commit it never heard of still makes it give the session up.
        await using (var source = await machines.CreateSourceHostAsync(provider))
        {
            var moves = new SessionMoveService(source, "Windows PC");
            var recovered = (await moves.RecoverAsync(new LocalSessionMoveArbiter(new SessionMoveTransactionStore(machines.TargetOptions)), CancellationToken.None)).Single();
            Assert.AreEqual(SessionMoveState.Finished, recovered.Status);
            Assert.IsTrue(source.RuntimeService.Relinquishments.Read(sessionId)!.IsRelinquished);
        }

        await using (var target = await machines.CreateTargetHostAsync(provider))
        {
            var moves = new SessionMoveService(target, "Mac mini");
            var recovered = (await moves.RecoverAsync(new LocalSessionMoveArbiter(moves.Store), CancellationToken.None)).Single();
            Assert.AreEqual(SessionMoveState.Finished, recovered.Status, string.Join(" ", recovered.Warnings));
            await SendAsync(target, sessionId, "Here now.");
            Assert.AreEqual(recovered.Transaction!.WorkFolder, provider.LastWorkingDirectory);
        }
    }

    private static async Task SendAsync(CodeAltaHost host, string sessionId, string prompt)
    {
        var result = await TrySendAsync(host, sessionId, prompt);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
    }

    private static async Task<OwnedSessionCommandResult> TrySendAsync(CodeAltaHost host, string sessionId, string prompt)
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
        Assert.IsNotNull(receipt);
        return await receipt.Completion.WaitAsync(TestDeadlines.Hang);
    }

    // Stands for the end of the process: nothing after it runs.
    private sealed class SimulatedCrashException : Exception;

    // Two machines as two profiles of this computer, each with a clone of one origin as its project.
    private sealed class Machines : IDisposable
    {
        private Machines(string root)
        {
            Root = root;
            Origin = Path.Combine(root, "origin.git");
            SourceGlobal = Directory.CreateDirectory(Path.Combine(root, "source", "global")).FullName;
            SourceProject = Path.Combine(root, "source", "project");
            TargetGlobal = Directory.CreateDirectory(Path.Combine(root, "target", "global")).FullName;
            TargetProject = Path.Combine(root, "target", "project");
        }

        public string Root { get; }
        public string Origin { get; }
        public string SourceGlobal { get; }
        public string SourceProject { get; }
        public string TargetGlobal { get; }
        public string TargetProject { get; }
        public CatalogOptions TargetOptions => new() { GlobalRoot = TargetGlobal };

        public static Machines Create()
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta.Move." + Guid.NewGuid().ToString("N")[..12])).FullName;
            if (Directory.ResolveLinkTarget(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), returnFinalTarget: true) is { } link)
                root = Path.Combine(link.FullName, Path.GetFileName(root));
            var machines = new Machines(root);
            try
            {
                machines.Git(root, "init", "-q", "--bare", "-b", "main", machines.Origin);
            }
            catch (Win32Exception)
            {
                machines.Dispose();
                Assert.Inconclusive("git is not installed.");
            }

            machines.Git(root, "clone", "-q", machines.Origin, machines.SourceProject);
            machines.Configure(machines.SourceProject);
            machines.Write(machines.SourceProject, "readme.md", "one\n");
            machines.Commit(machines.SourceProject, "One");
            machines.Git(machines.SourceProject, "push", "-q", "-u", "origin", "main");
            machines.Git(root, "clone", "-q", machines.Origin, machines.TargetProject);
            machines.Configure(machines.TargetProject);
            return machines;
        }

        public Task<CodeAltaHost> CreateSourceHostAsync(RecordingProvider provider, string owner = SessionLeaseOwner.DesktopKind, bool readOnlyGlobalState = false)
            => CreateHostAsync(SourceGlobal, SourceProject, provider, owner, readOnlyGlobalState);

        // The target knows its project, as a machine that has the repository does.
        public async Task<CodeAltaHost> CreateTargetHostAsync(RecordingProvider provider)
        {
            var host = await CreateHostAsync(TargetGlobal, TargetProject, provider, SessionLeaseOwner.DesktopKind, false);
            await host.ProjectCatalog.EnsurePersistedAsync(host.CurrentProject);
            return host;
        }

        // Carries a bundle as the network will: one archive stream, unpacked on the other side.
        public async Task<string> TransferAsync(string bundle, string received)
        {
            using var stream = new MemoryStream();
            await SessionBundle.PackAsync(bundle, stream, CancellationToken.None);
            stream.Position = 0;
            await SessionBundle.UnpackAsync(stream, received, CancellationToken.None);
            return received;
        }

        public void Write(string root, string relative, string content)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Commit(string root, string message)
        {
            Git(root, "add", "-A");
            Git(root, "commit", "-q", "-m", message);
        }

        public string Git(string folder, params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
            return output;
        }

        private void Configure(string root)
        {
            foreach (var (name, value) in new[] { ("user.name", "Test"), ("user.email", "test@example.invalid"), ("core.autocrlf", "false"), ("commit.gpgsign", "false") })
                Git(root, "config", name, value);
        }

        private static Task<CodeAltaHost> CreateHostAsync(string global, string project, RecordingProvider provider, string owner, bool readOnlyGlobalState)
            => CodeAltaHost.CreateAsync(
                new CodeAltaHostOptions
                {
                    GlobalRoot = global,
                    CurrentProjectPath = project,
                    IsHeadless = true,
                    AutoApproveOwnedPermissions = true,
                    OwnedCommandReceiptCapacity = 32,
                    SessionLeaseOwner = owner,
                    ReadOnlyGlobalState = readOnlyGlobalState,
                    ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
                },
                CancellationToken.None);

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of temporary files.
            }
        }
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task Released => _released.Task;

        public void Start() => _started.TrySetResult();

        public void Release() => _released.TrySetResult();
    }

    // A provider that records its turns, and, with a root for its transcripts, keeps its own copy of the conversation
    // under a folder named after the working folder, as Claude Code does.
    private sealed class RecordingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor, IAgentSessionTransferProvider
    {
        private Gate? _gate;

        public string? TranscriptRoot { get; init; }

        public string? LastWorkingDirectory { get; private set; }

        public string LastUserText { get; private set; } = string.Empty;

        public List<int> Conversations { get; } = [];

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-move"), "Fake Move") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-move", DisplayName = "Fake Move", TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public Gate Block() => _gate = new Gate();

        public void WriteTranscript(string workingDirectory, string content)
        {
            var path = TranscriptPath(TranscriptRoot!, workingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public string? ReadTranscript(string workingDirectory)
            => File.Exists(TranscriptPath(TranscriptRoot!, workingDirectory)) ? File.ReadAllText(TranscriptPath(TranscriptRoot!, workingDirectory)) : null;

        private static string TranscriptPath(string root, string workingDirectory)
            => Path.Combine(root, string.Concat(workingDirectory.Select(static c => char.IsAsciiLetterOrDigit(c) ? c : '-')), "transcript.jsonl");

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };

        public IModelProviderTurnExecutor CreateTurnExecutor() => this;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo("fake-model", DisplayName: "Fake Model")],
                SelectedModelId = "fake-model",
            });

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            lock (Conversations)
            {
                LastWorkingDirectory = request.WorkingDirectory;
                var user = request.Conversation.Last(static message => message.Role == AgentConversationRole.User);
                LastUserText = string.Join("\n", user.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
                Conversations.Add(request.Conversation.Count(static message => message.Role == AgentConversationRole.User));
            }

            if (Interlocked.Exchange(ref _gate, null) is { } gate)
            {
                gate.Start();
                await gate.Released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]),
            };
        }

        public Task<(bool Copied, string? Warning)> ExportSessionAsync(AgentSessionState state, string workingDirectory, string destination,
            CancellationToken cancellationToken = default)
        {
            if (TranscriptRoot is null || !File.Exists(TranscriptPath(TranscriptRoot, workingDirectory))) return Task.FromResult((false, (string?)null));
            Directory.CreateDirectory(destination);
            File.Copy(TranscriptPath(TranscriptRoot, workingDirectory), Path.Combine(destination, "transcript.jsonl"));
            return Task.FromResult((true, (string?)null));
        }

        public Task<string?> ImportSessionAsync(AgentSessionState state, string source, string workingDirectory, CancellationToken cancellationToken = default)
        {
            if (TranscriptRoot is null) return Task.FromResult<string?>("No profile.");
            var path = TranscriptPath(TranscriptRoot, workingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(source, "transcript.jsonl"), path, overwrite: true);
            return Task.FromResult<string?>(null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
