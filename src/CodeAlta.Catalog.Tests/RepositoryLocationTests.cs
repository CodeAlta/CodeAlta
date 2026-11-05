using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Catalog.SessionMoves;

namespace CodeAlta.Catalog.Tests;

/// <summary>Where a project folder is in its repository, which recognizes the same project on other machines, and the cache of it.</summary>
[TestClass]
public sealed class RepositoryLocationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ReadLocation_GivesTheKeyOfTheOrigin_AndThePathOfTheFolderInTheCheckout()
    {
        var home = NewFolder();
        try
        {
            var root = Path.Combine(home, "checkout");
            Git(home, "init", "-q", root);
            Git(root, "remote", "add", "origin", "git@github.com:CodeAlta/Sample.git");
            var sub = Directory.CreateDirectory(Path.Combine(root, "tools", "cli")).FullName;
            var plain = Directory.CreateDirectory(Path.Combine(home, "plain")).FullName;
            var bare = Path.Combine(home, "no-remote");
            Git(home, "init", "-q", bare);

            Assert.AreEqual(new RepositoryLocation("github.com/codealta/sample", ""), await RepositoryKey.ReadLocationAsync(root, TestContext.CancellationToken));
            Assert.AreEqual(new RepositoryLocation("github.com/codealta/sample", "tools/cli"), await RepositoryKey.ReadLocationAsync(sub, TestContext.CancellationToken),
                "A project of a subfolder is the repository and its path in it.");
            Assert.IsNull(await RepositoryKey.ReadLocationAsync(bare, TestContext.CancellationToken), "A repository with no remote has no key.");
            Assert.IsNull(await RepositoryKey.ReadLocationAsync(Path.Combine(home, "missing"), TestContext.CancellationToken));
            // A folder outside any repository: git may find one above the temporary folder, never this key.
            Assert.AreNotEqual("github.com/codealta/sample", (await RepositoryKey.ReadLocationAsync(plain, TestContext.CancellationToken))?.Key);
        }
        finally
        {
            Delete(home);
        }
    }

    [TestMethod]
    public void SameLocation_IsTheSameKeyAndPath_WhateverTheCaseOfThePath()
    {
        var root = new RepositoryLocation("github.com/a/b", "");
        Assert.IsTrue(RepositoryKey.SameLocation(root, new RepositoryLocation("github.com/a/b", "")));
        Assert.IsTrue(RepositoryKey.SameLocation(new RepositoryLocation("github.com/a/b", "Tools/CLI"), new RepositoryLocation("github.com/a/b", "tools/cli")));
        Assert.IsFalse(RepositoryKey.SameLocation(root, new RepositoryLocation("github.com/a/b", "tools")), "A subfolder is another project.");
        Assert.IsFalse(RepositoryKey.SameLocation(root, new RepositoryLocation("github.com/a/c", "")));
        Assert.IsFalse(RepositoryKey.SameLocation(root, null));
        Assert.IsFalse(RepositoryKey.SameLocation(null, null), "Two folders of no repository are not the same project.");
    }

    [TestMethod]
    public async Task Cache_ReadsAFolderOnce_ThenAgainOnceItsLocationIsOld()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var reads = 0;
        var gate = new TaskCompletionSource();
        var cache = new RepositoryLocationCache(TimeSpan.FromMinutes(5), clock, async (folder, _) =>
        {
            Interlocked.Increment(ref reads);
            await gate.Task;
            return new RepositoryLocation("github.com/a/b", folder.EndsWith("sub", StringComparison.Ordinal) ? "sub" : "");
        });

        var first = cache.GetAsync("/repo", TestContext.CancellationToken);
        var second = cache.GetAsync("/repo", TestContext.CancellationToken);
        gate.SetResult();
        Assert.AreEqual("", (await first)!.Path);
        Assert.AreEqual("", (await second)!.Path);
        Assert.AreEqual(1, reads, "Callers of the same folder at once share one read.");

        Assert.AreEqual("sub", (await cache.GetAsync("/repo/sub", TestContext.CancellationToken))!.Path);
        Assert.AreEqual(2, reads);
        _ = await cache.GetAsync("/repo", TestContext.CancellationToken);
        Assert.AreEqual(2, reads, "A recent location is not read again.");

        clock.Advance(TimeSpan.FromMinutes(6));
        _ = await cache.GetAsync("/repo", TestContext.CancellationToken);
        Assert.AreEqual(3, reads, "An old location is read again.");
    }

    [TestMethod]
    public async Task Cache_GivesNoLocation_ForAFolderGitCannotRead_AndACanceledWaitLeavesTheRead()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var gate = new TaskCompletionSource();
        var cache = new RepositoryLocationCache(TimeSpan.FromMinutes(5), clock, async (folder, _) =>
        {
            if (folder == "/broken") throw new Win32Exception("git is not installed");
            await gate.Task;
            return new RepositoryLocation("github.com/a/b", "");
        });

        Assert.IsNull(await cache.GetAsync("/broken", TestContext.CancellationToken));
        using var canceled = new CancellationTokenSource();
        var waiting = cache.GetAsync("/slow", canceled.Token);
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        gate.SetResult();
        Assert.IsNotNull(await cache.GetAsync("/slow", TestContext.CancellationToken), "The read went on for the next caller.");
    }

    private static string NewFolder()
    {
        var home = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-location-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Git(home, "--version");
        }
        catch (Win32Exception)
        {
            Delete(home);
            Assert.Inconclusive("git is not installed.");
        }

        return home;
    }

    private static void Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
    }

    private static void Delete(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of temporary files.
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }
}
