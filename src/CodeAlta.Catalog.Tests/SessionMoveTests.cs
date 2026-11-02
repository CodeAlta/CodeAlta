using System.ComponentModel;
using System.Diagnostics;
using System.Text;

using CodeAlta.Catalog.SessionMoves;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The parts of a move of a session to another machine that need no runtime: the repository key, the git payload, the
/// bundle files, the relinquishment beside the lease and the records of the transaction.
/// </summary>
[TestClass]
public sealed class SessionMoveTests
{
    [TestMethod]
    [DataRow("git@github.com:CodeAlta/CodeAlta.git", "github.com/codealta/codealta")]
    [DataRow("https://github.com/CodeAlta/CodeAlta", "github.com/codealta/codealta")]
    [DataRow("ssh://git@github.com/codealta/codealta.git", "github.com/codealta/codealta")]
    [DataRow("https://user:token@GitHub.com:443/CodeAlta/CodeAlta.git/", "github.com/codealta/codealta")]
    [DataRow("ssh://git@github.com:22/CodeAlta/CodeAlta", "github.com/codealta/codealta")]
    [DataRow("https://git.example.com:8443/Team/Repo.git", "git.example.com:8443/Team/Repo")]
    [DataRow("git@git.example.com:Team/Repo.git", "git.example.com/Team/Repo")]
    [DataRow("https://dev.azure.com/Org/Project/_git/Repo", "dev.azure.com/org/project/repo")]
    [DataRow("https://Org@dev.azure.com/Org/Project/_git/Repo", "dev.azure.com/org/project/repo")]
    [DataRow("git@ssh.dev.azure.com:v3/Org/Project/Repo", "dev.azure.com/org/project/repo")]
    [DataRow("https://org.visualstudio.com/DefaultCollection/Project/_git/Repo", "dev.azure.com/org/project/repo")]
    [DataRow("https://gitlab.com/Group/Sub/Repo.git", "gitlab.com/group/sub/repo")]
    public void RepositoryKeys_OfOneRepository_AreOne(string url, string key)
        => Assert.AreEqual(key, RepositoryKey.Normalize(url));

    [TestMethod]
    public void RepositoryKeys_OfNothing_AreNone()
    {
        Assert.IsNull(RepositoryKey.Normalize(null));
        Assert.IsNull(RepositoryKey.Normalize("  "));
        Assert.IsNull(RepositoryKey.Normalize("https://github.com/"));
        Assert.AreEqual(RepositoryKey.Normalize("/srv/git/repo.git"), RepositoryKey.Normalize("file:///srv/git/repo"));
    }

    [TestMethod]
    [DataRow(".env", true)]
    [DataRow("config/.env.local", true)]
    [DataRow("certs/server.PEM", true)]
    [DataRow("signing.key", true)]
    [DataRow("home/.ssh/id_ed25519", true)]
    [DataRow("infra/prod.tfvars", true)]
    [DataRow("src/environment.ts", false)]
    [DataRow("keys.md", false)]
    public void SecretLookingNames_AreRecognized(string path, bool secret) => Assert.AreEqual(secret, GitSessionPayload.LooksSecret(path));

    [TestMethod]
    public async Task APushedCleanBranch_ShipsNothing_AndTheTargetGetsItsHead()
    {
        using var repos = MoveRepositories.Create();
        var capture = await GitSessionPayload.CaptureAsync(repos.Source, repos.Bundle, GitSessionPayload.DefaultMaximumBytes, CancellationToken.None);

        Assert.AreEqual("ok", capture.Status, capture.Message);
        var repository = capture.Repository!;
        Assert.IsTrue(repository.Payload.IsEmpty);
        Assert.IsFalse(repository.Dirty);
        Assert.AreEqual("main", repository.Branch);
        Assert.AreEqual("origin/main", repository.Upstream);
        Assert.AreEqual(RepositoryKey.Normalize(repos.Origin), repository.Key);
        Assert.IsFalse(Directory.Exists(Path.Combine(repos.Bundle, "git")), "An empty payload leaves no folder.");

        var prepared = await GitSessionPayload.PrepareAsync(repos.Target, repository, repos.Bundle, repos.TargetWorktree, CancellationToken.None);

        Assert.AreEqual("ok", prepared.Status, prepared.Message);
        Assert.AreEqual(repository.Head, repos.GitIn(repos.TargetWorktree, "rev-parse", "HEAD").Trim());
        // main is checked out in the target's own checkout: the moved session gets a branch of its own.
        Assert.AreEqual("main-moved", prepared.Branch);
        Assert.IsTrue(prepared.CreatedBranch);
    }

    [TestMethod]
    public async Task UnpushedCommitsAndADirtyTree_Travel_AndSecretsStayBehind()
    {
        using var repos = MoveRepositories.Create();
        repos.GitIn(repos.Source, "checkout", "-q", "-b", "feature");
        repos.Write(repos.Source, "src/one.txt", "first unpushed\n");
        repos.Commit(repos.Source, "one");
        repos.Write(repos.Source, "src/two.txt", "second unpushed\n");
        repos.Commit(repos.Source, "two");
        // The work in progress: a change, a deletion, a new text file, a new binary file, and what stays behind.
        repos.Write(repos.Source, "readme.md", "changed\n");
        File.Delete(Path.Combine(repos.Source, "src", "one.txt"));
        repos.Write(repos.Source, "notes/new.md", "new\n");
        File.WriteAllBytes(Path.Combine(repos.Source, "image.bin"), [0, 1, 2, 255, 0, 13, 10, 0]);
        repos.Write(repos.Source, ".env", "TOKEN=secret\n");
        repos.Write(repos.Source, "certs/server.pem", "-----BEGIN-----\n");
        repos.Write(repos.Source, "bin/output.dll", "ignored\n");

        var capture = await GitSessionPayload.CaptureAsync(repos.Source, repos.Bundle, GitSessionPayload.DefaultMaximumBytes, CancellationToken.None);

        Assert.AreEqual("ok", capture.Status, capture.Message);
        var repository = capture.Repository!;
        Assert.AreEqual("feature", repository.Branch);
        Assert.IsNull(repository.Upstream);
        Assert.IsTrue(repository.Dirty);
        Assert.AreEqual(2, repository.Payload.UnpushedCommits);
        Assert.IsNotNull(repository.Payload.CommitsFile);
        Assert.IsNotNull(repository.Payload.PatchFile);
        var patch = File.ReadAllText(Path.Combine(repos.Bundle, repository.Payload.PatchFile!));
        Assert.IsFalse(patch.Contains("TOKEN=secret", StringComparison.Ordinal), "A secret never enters the bundle.");
        CollectionAssert.AreEquivalent(new[] { ".env:secret", "certs/server.pem:secret", "bin:ignored" },
            repository.Payload.LeftBehind.Select(static file => file.Path + ":" + file.Reason).ToArray());
        Assert.AreEqual(string.Empty, repos.GitIn(repos.Source, "diff", "--cached", "--name-only"), "The index of the source is not touched.");

        var prepared = await GitSessionPayload.PrepareAsync(repos.Target, repository, repos.Bundle, repos.TargetWorktree, CancellationToken.None);

        Assert.AreEqual("ok", prepared.Status, prepared.Message);
        Assert.AreEqual("feature", prepared.Branch);
        var tree = repos.TargetWorktree;
        Assert.AreEqual(repository.Head, repos.GitIn(tree, "rev-parse", "HEAD").Trim());
        Assert.AreEqual("changed\n", File.ReadAllText(Path.Combine(tree, "readme.md")));
        Assert.IsFalse(File.Exists(Path.Combine(tree, "src", "one.txt")));
        Assert.AreEqual("second unpushed\n", File.ReadAllText(Path.Combine(tree, "src", "two.txt")));
        Assert.AreEqual("new\n", File.ReadAllText(Path.Combine(tree, "notes", "new.md")));
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 255, 0, 13, 10, 0 }, File.ReadAllBytes(Path.Combine(tree, "image.bin")));
        Assert.IsFalse(File.Exists(Path.Combine(tree, ".env")));
        Assert.IsFalse(File.Exists(Path.Combine(tree, "certs", "server.pem")));
        // The changes are changes there too: the index of the worktree is at HEAD.
        var status = repos.GitIn(tree, "status", "--porcelain", "--untracked-files=all");
        StringAssert.Contains(status, " M readme.md");
        StringAssert.Contains(status, " D src/one.txt");
        StringAssert.Contains(status, "?? notes/new.md");
    }

    [TestMethod]
    public async Task APayloadOverTheLimit_IsRefused()
    {
        using var repos = MoveRepositories.Create();
        repos.Write(repos.Source, "big.txt", new string('x', 4096));

        var capture = await GitSessionPayload.CaptureAsync(repos.Source, repos.Bundle, maximumBytes: 100, CancellationToken.None);

        Assert.AreEqual("payload_too_large", capture.Status);
        Assert.IsNull(capture.Repository);
    }

    [TestMethod]
    public async Task AChangedPayload_IsRefusedByTheTarget_AndLeavesNoWorktree()
    {
        using var repos = MoveRepositories.Create();
        repos.Write(repos.Source, "readme.md", "changed\n");
        var capture = await GitSessionPayload.CaptureAsync(repos.Source, repos.Bundle, GitSessionPayload.DefaultMaximumBytes, CancellationToken.None);
        var repository = capture.Repository! with { Payload = capture.Repository!.Payload with { Tree = new string('0', 40) } };

        var prepared = await GitSessionPayload.PrepareAsync(repos.Target, repository, repos.Bundle, repos.TargetWorktree, CancellationToken.None);

        Assert.AreEqual("tree_mismatch", prepared.Status);
        Assert.IsFalse(Directory.Exists(repos.TargetWorktree));
        Assert.IsFalse(repos.GitIn(repos.Target, "branch", "--list", "main-moved").Contains("main-moved", StringComparison.Ordinal), "The branch it made goes too.");
    }

    [TestMethod]
    public async Task ABundle_IsVerified_PackedAndUnpacked()
    {
        using var repos = MoveRepositories.Create();
        var journal = Encoding.UTF8.GetBytes("{\"a\":1}\n");
        File.WriteAllBytes(Path.Combine(repos.Home, "journal"), journal);
        var file = await SessionBundle.CopyIntoAsync(Path.Combine(repos.Home, "journal"), repos.Bundle, SessionMoveManifest.JournalFile, CancellationToken.None);
        repos.Write(repos.Bundle, "attachments/image.png", "png");
        var manifest = await SessionBundle.WriteManifestAsync(repos.Bundle, Manifest(file), CancellationToken.None);
        Assert.AreEqual(2, manifest.Files.Count);
        Assert.IsNull(await SessionBundle.VerifyAsync(repos.Bundle, manifest, CancellationToken.None));

        using var archive = new MemoryStream();
        await SessionBundle.PackAsync(repos.Bundle, archive, CancellationToken.None);
        archive.Position = 0;
        var unpacked = Path.Combine(repos.Home, "unpacked");
        await SessionBundle.UnpackAsync(archive, unpacked, CancellationToken.None);
        var read = await SessionBundle.ReadManifestAsync(unpacked, CancellationToken.None);
        Assert.IsNotNull(read);
        Assert.IsNull(await SessionBundle.VerifyAsync(unpacked, read, CancellationToken.None));

        File.AppendAllText(Path.Combine(unpacked, "journal.jsonl"), "tampered");
        StringAssert.Contains(await SessionBundle.VerifyAsync(unpacked, read, CancellationToken.None), "journal.jsonl");
        File.WriteAllText(Path.Combine(unpacked, "extra.txt"), "x");
        Assert.IsNotNull(await SessionBundle.VerifyAsync(unpacked, read with { Files = [.. read.Files.Where(static f => f.Path != "journal.jsonl")] }, CancellationToken.None));
    }

    [TestMethod]
    public void ARelinquishment_IsKeptBesideTheLease_AndOnlyItsMoveUnfreezesIt()
    {
        using var repos = MoveRepositories.Create();
        var store = new SessionRelinquishmentStore(Path.Combine(repos.Home, "leases"));

        Assert.IsNull(store.Read("session-1"));
        Assert.IsTrue(store.Freeze("session-1", "move-a", "Mac mini"));
        Assert.IsFalse(store.Freeze("session-1", "move-b", "Laptop"), "Another move cannot take a frozen session.");
        Assert.IsFalse(store.Unfreeze("session-1", "move-b"));
        Assert.AreEqual(SessionRelinquishment.Frozen, store.Read("session-1")!.State);
        Assert.IsTrue(store.Unfreeze("session-1", "move-a"));
        Assert.IsNull(store.Read("session-1"));

        store.Relinquish("session-1", "move-c", "Mac mini", 3);
        var record = store.Read("session-1")!;
        Assert.IsTrue(record.IsRelinquished);
        Assert.AreEqual((3L, "Mac mini"), (record.Epoch, record.ToMachine));
        Assert.IsFalse(store.Unfreeze("session-1", "move-c"), "A session given away is not unfrozen.");

        File.WriteAllText(store.GetPath("session-2"), "{ not json");
        Assert.IsTrue(store.Read("session-2")!.IsRelinquished, "A damaged record keeps the session read-only.");
    }

    [TestMethod]
    public void TheFirstDecisionOfAnArbiter_Stands()
    {
        using var repos = MoveRepositories.Create();
        var store = new SessionMoveTransactionStore(Path.Combine(repos.Home, "moves"));
        store.Save(new SessionMoveTransaction { MoveId = "m1", Role = SessionMoveRole.Target, State = SessionMoveState.Prepared, SessionId = "s1" });
        store.Save(new SessionMoveTransaction { MoveId = "m2", Role = SessionMoveRole.Source, State = SessionMoveState.Finished, SessionId = "s2" });

        Assert.AreEqual("m1", store.ListUnfinished().Single().MoveId);
        Assert.IsNull(store.ReadDecision("m1"));
        Assert.AreEqual(SessionMoveState.Aborted, store.Decide("m1", SessionMoveState.Aborted, 0).Outcome);
        Assert.AreEqual(SessionMoveState.Aborted, store.Decide("m1", SessionMoveState.Committed, 1).Outcome, "A commit after an abort is refused.");
        Assert.AreEqual(SessionMoveState.Committed, store.Decide("m3", SessionMoveState.Committed, 4).Outcome);
        Assert.AreEqual(4, store.ReadDecision("m3")!.Epoch);
        Assert.ThrowsExactly<ArgumentException>(() => store.Decide("../m4", SessionMoveState.Committed, 1));
    }

    private static SessionMoveManifest Manifest(SessionMoveFile journal) => new()
    {
        MoveId = "move-1",
        SessionId = "session-1",
        Journal = journal,
        WorkingFolder = "C:/code/x",
        Repository = new SessionMoveRepository { Key = "github.com/a/b", SourceRoot = "C:/code/x", Head = "abc", Payload = new SessionMoveGitPayload { Tree = "def" } },
    };

    // A bare origin with one commit on main, cloned twice: the checkout of the source and the one of the target.
    private sealed class MoveRepositories : IDisposable
    {
        private MoveRepositories(string home)
        {
            Home = home;
            Origin = Path.Combine(home, "origin.git");
            Source = Path.Combine(home, "source");
            Target = Path.Combine(home, "target");
            Bundle = Directory.CreateDirectory(Path.Combine(home, "bundle")).FullName;
            TargetWorktree = Path.Combine(home, "worktrees", "moved");
        }

        public string Home { get; }
        public string Origin { get; }
        public string Source { get; }
        public string Target { get; }
        public string Bundle { get; }
        public string TargetWorktree { get; }

        public static MoveRepositories Create()
        {
            var home = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-move-" + Guid.NewGuid().ToString("N"))).FullName;
            if (Directory.ResolveLinkTarget(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), returnFinalTarget: true) is { } target)
                home = Path.Combine(target.FullName, Path.GetFileName(home));
            var repos = new MoveRepositories(home);
            try
            {
                repos.GitIn(home, "init", "-q", "--bare", "-b", "main", repos.Origin);
            }
            catch (Win32Exception)
            {
                repos.Dispose();
                Assert.Inconclusive("git is not installed.");
            }

            repos.GitIn(home, "clone", "-q", repos.Origin, repos.Source);
            repos.Configure(repos.Source);
            repos.Write(repos.Source, "readme.md", "one\n");
            repos.Write(repos.Source, ".gitignore", "bin/\n");
            repos.Commit(repos.Source, "one");
            repos.GitIn(repos.Source, "push", "-q", "-u", "origin", "main");
            repos.GitIn(home, "clone", "-q", repos.Origin, repos.Target);
            repos.Configure(repos.Target);
            return repos;
        }

        public void Write(string root, string relative, string content)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Commit(string root, string message)
        {
            GitIn(root, "add", "-A");
            GitIn(root, "commit", "-q", "-m", message);
        }

        public string GitIn(string folder, params string[] arguments)
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
                GitIn(root, "config", name, value);
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Home, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of temporary files.
            }
        }
    }
}
