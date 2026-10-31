using System.Diagnostics;
using System.Text;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// Whoever runs a session holds its lease: a second holder, in this process or another, is refused, and the lease of a
/// process that ended can be taken again without cleanup.
/// </summary>
[TestClass]
public sealed class SessionLeaseStoreTests
{
    private const string SessionId = "01a172ef-a00c-7eb7-a7b0-4c23786c528f";
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void ALease_IsTakenAndReleased_AndItsFileGoesWithIt()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(Path.Combine(fixture.Root, "sessions", "leases"), fixture.Store.LeasesDirectory);

        var lease = fixture.Store.TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run-1"));
        Assert.IsNotNull(lease);
        Assert.AreEqual(SessionId, lease.SessionId);
        Assert.AreEqual(Path.Combine(fixture.Store.LeasesDirectory, SessionId + ".lease"), lease.Path);
        Assert.IsTrue(File.Exists(lease.Path));
        Assert.AreEqual(Environment.ProcessId, lease.Holder.ProcessId);
        Assert.AreEqual(Now, lease.Holder.AcquiredUtc);

        lease.Dispose();
        Assert.IsTrue(lease.IsReleased);
        Assert.IsFalse(File.Exists(lease.Path));
        lease.Dispose();

        using var again = fixture.Store.TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.DesktopKind, "window"));
        Assert.IsNotNull(again, "A released lease is taken again.");
    }

    [TestMethod]
    public void ASecondAcquisition_FailsWhileTheLeaseIsHeld_InThisProcessToo()
    {
        using var fixture = new Fixture();
        using var lease = fixture.Store.TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run-1"));
        Assert.IsNotNull(lease);

        Assert.IsNull(fixture.Store.TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run-2")));
        Assert.IsNull(new SessionLeaseStore(fixture.Options).TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.TuiKind, "tui")),
            "Another store of the process is refused like a foreign holder.");
        using var other = fixture.Store.TryAcquire("another-session", new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run-1"));
        Assert.IsNotNull(other, "Each session has a lease of its own.");
    }

    [TestMethod]
    public void TheHolder_IsReadWhileTheLeaseIsHeld_AndNothingOnceItIsFree()
    {
        using var fixture = new Fixture();
        Assert.IsNull(fixture.Store.TryReadHolder(SessionId), "No file: free.");
        Assert.AreEqual(0, fixture.Store.ListHeld().Count);

        var lease = fixture.Store.TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.DesktopKind, "window-7"));
        Assert.IsNotNull(lease);
        var holder = fixture.Store.TryReadHolder(SessionId);
        Assert.IsNotNull(holder);
        Assert.AreEqual(SessionLeaseOwner.DesktopKind, holder.OwnerKind);
        Assert.AreEqual(Environment.ProcessId, holder.ProcessId);
        Assert.AreEqual("window-7", holder.InstanceId);
        Assert.AreEqual(Now, holder.AcquiredUtc);
        Assert.IsNotNull(holder.ProcessStartUtc);
        Assert.AreEqual(lease.Holder, holder);
        Assert.IsFalse(lease.IsReleased, "Reading the holder leaves the lease as it is.");
        Assert.IsNull(fixture.Store.TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run")));

        File.WriteAllText(Path.Combine(fixture.Store.LeasesDirectory, "not a lease.txt"), "x");
        CollectionAssert.AreEqual(new[] { holder }, fixture.Store.ListHeld().ToArray());

        lease.Dispose();
        Assert.IsNull(fixture.Store.TryReadHolder(SessionId));
        Assert.AreEqual(0, fixture.Store.ListHeld().Count);
    }

    [TestMethod]
    public void AnotherHandle_HoldsTheLeaseLikeAnotherProcess_AndItsFileIsFreeOnceClosed()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Store.LeasesDirectory);
        var path = fixture.Store.GetLeasePath(SessionId);

        // A holder that is not this store, opening the file as every holder does.
        using (var foreign = OpenAsHolder(path))
        {
            foreign.Write("{\"ownerKind\":\"tui\",\"pid\":4242,\"instanceId\":\"other\"}"u8);
            foreign.Flush();
            Assert.IsNull(fixture.Store.TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run")));
            var holder = fixture.Store.TryReadHolder(SessionId);
            Assert.AreEqual("tui", holder?.OwnerKind);
            Assert.AreEqual(4242, holder?.ProcessId);
        }

        // Closed without deleting its file, as when the holder crashes: the file is free, whatever it says.
        Assert.IsTrue(File.Exists(path));
        Assert.IsNull(fixture.Store.TryReadHolder(SessionId));
        Assert.AreEqual(0, fixture.Store.ListHeld().Count);
        using var lease = fixture.Store.TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run"));
        Assert.IsNotNull(lease);
        Assert.AreEqual(SessionLeaseOwner.CliKind, fixture.Store.TryReadHolder(SessionId)?.OwnerKind, "The new holder writes its own content.");
    }

    [TestMethod]
    public void AHeldLease_WithoutContentYet_IsHeldByAnUnknownHolder()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Store.LeasesDirectory);
        using var foreign = OpenAsHolder(fixture.Store.GetLeasePath(SessionId));
        var holder = fixture.Store.TryReadHolder(SessionId);
        Assert.IsNotNull(holder);
        Assert.IsNull(holder.OwnerKind);
        Assert.AreEqual(SessionId, holder.SessionId);
    }

    [TestMethod]
    public void ASessionId_MustNameAFile()
    {
        using var fixture = new Fixture();
        var owner = new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run");
        Assert.IsTrue(SessionLeaseStore.IsValidSessionId(SessionId));
        Assert.IsTrue(SessionLeaseStore.IsValidSessionId("my-run_2.v1"));
        foreach (var invalid in new[] { "", " ", "a/b", "a\\b", "..", ".", "a:b", "a*b", "con", "CON.x", "nul", "a\nb", " a", "a.", new string('a', 129) })
        {
            Assert.IsFalse(SessionLeaseStore.IsValidSessionId(invalid), invalid);
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Store.TryAcquire(invalid, owner), invalid);
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Store.TryReadHolder(invalid), invalid);
        }

        Assert.ThrowsExactly<ArgumentNullException>(() => fixture.Store.TryAcquire(null!, owner));
        Assert.ThrowsExactly<ArgumentNullException>(() => fixture.Store.TryAcquire(SessionId, null!));
        Assert.ThrowsExactly<ArgumentException>(() => new SessionLeaseOwner(" ", "run"));
        Assert.ThrowsExactly<ArgumentException>(() => new SessionLeaseOwner("cli", "a\u0001"));
        Assert.IsFalse(Directory.Exists(fixture.Store.LeasesDirectory), "Nothing is created for an invalid id.");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnotherProcess_HoldsTheLease_UntilItEnds_ThenItIsTakenWithoutCleanup()
    {
        var shell = FindPowerShell();
        if (shell is null) Assert.Inconclusive("PowerShell (pwsh) is not on the PATH: the holder process cannot be started.");
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Store.LeasesDirectory);
        var path = fixture.Store.GetLeasePath(SessionId);
        var share = OperatingSystem.IsWindows() ? "Read" : "None";
        var script = $$"""
            $s = [IO.FileStream]::new('{{path.Replace("'", "''", StringComparison.Ordinal)}}', 'OpenOrCreate', 'ReadWrite', '{{share}}')
            $b = [Text.Encoding]::UTF8.GetBytes('{"ownerKind":"desktop","pid":' + $PID + ',"instanceId":"child"}')
            $s.Write($b, 0, $b.Length); $s.Flush()
            [Console]::Out.WriteLine('HELD'); [Console]::Out.Flush()
            [Console]::In.ReadLine() | Out-Null
            """;
        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            start.ArgumentList.Add(argument);
        }

        using var child = Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var line = await child.StandardOutput.ReadLineAsync(timeout.Token);
            if (line != "HELD") Assert.Fail($"The holder did not start: {line} {await child.StandardError.ReadToEndAsync(timeout.Token)}");

            Assert.IsNull(fixture.Store.TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run")));
            var holder = fixture.Store.TryReadHolder(SessionId);
            Assert.AreEqual("desktop", holder?.OwnerKind);
            Assert.AreEqual(child.Id, holder?.ProcessId);
            Assert.AreEqual(1, fixture.Store.ListHeld().Count);

            // The holder dies without releasing anything.
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }

        Assert.IsTrue(File.Exists(path), "The file of the crashed holder stays.");
        // Its exit is seen before Windows has closed its handles: under load, its file still reads as held for a moment.
        // A handle that is never closed still fails below, once the wait is over.
        var waited = Stopwatch.StartNew();
        while (fixture.Store.TryReadHolder(SessionId) is not null && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        Assert.IsNull(fixture.Store.TryReadHolder(SessionId), "The lease of the ended holder is free once its handles are closed.");
        using var lease = fixture.Store.TryAcquire(SessionId, new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run"));
        Assert.IsNotNull(lease, "The lease of a process that ended is taken again at once.");
    }

    private static FileStream OpenAsHolder(string path)
        => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None);

    private static string? FindPowerShell()
    {
        var name = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "codealta-session-lease-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Options = new CatalogOptions { GlobalRoot = Root };
            Store = new SessionLeaseStore(Options.SessionLeasesRoot, new FixedTime(Now));
        }

        public string Root { get; }

        public CatalogOptions Options { get; }

        public SessionLeaseStore Store { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
