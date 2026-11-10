using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginChangeMonitorTests
{
    [TestMethod]
    public async Task FileSystemMonitorDebouncesPackageChanges()
    {
        using var temp = new TestTempDirectory();
        var packageDirectory = Path.Combine(temp.Path, "hello");
        Directory.CreateDirectory(packageDirectory);
        var root = new PluginRoot { RootPath = temp.Path, Scope = PluginScope.Global };
        await using var monitor = new FileSystemPluginChangeMonitor([root], TimeSpan.FromMilliseconds(100));
        var changes = new List<PluginSourceChange>();
        using var signal = new ManualResetEventSlim();
        monitor.Changed += (_, change) =>
        {
            lock (changes)
            {
                changes.Add(change);
            }

            signal.Set();
        };

        monitor.Start();
        File.WriteAllText(Path.Combine(packageDirectory, "plugin.cs"), "// changed");

        Assert.IsTrue(signal.Wait(TestTime.Seconds(5)));
        PluginSourceChange[] snapshot;
        lock (changes)
        {
            snapshot = [.. changes];
        }

        Assert.IsTrue(snapshot.Any(change => change.PackageId == "hello"));
        Assert.IsTrue(monitor.PendingChanges.Any(change => change.PackageId == "hello"));
    }

    [TestMethod]
    public async Task FileSystemMonitorTracksDeletedRenamedRootChangesAndCleanup()
    {
        using var temp = new TestTempDirectory();
        var packageDirectory = Path.Combine(temp.Path, "hello");
        Directory.CreateDirectory(packageDirectory);
        var source = Path.Combine(packageDirectory, "plugin.cs");
        File.WriteAllText(source, "// initial");
        var root = new PluginRoot { RootPath = temp.Path, Scope = PluginScope.Global };
        await using var monitor = new FileSystemPluginChangeMonitor([root], TimeSpan.FromMilliseconds(50));
        using var signal = new ManualResetEventSlim();
        monitor.Changed += (_, _) => signal.Set();

        monitor.Start();
        File.Move(source, Path.Combine(packageDirectory, "plugin2.cs"));

        Assert.IsTrue(signal.Wait(TestTime.Seconds(5)));
        Assert.IsTrue(monitor.PendingChanges.Any(change => change.PackageId == "hello"));
        monitor.MarkProcessed("hello");
        Assert.IsFalse(monitor.PendingChanges.Any(change => change.PackageId == "hello"));
        signal.Reset();

        File.WriteAllText(Path.Combine(temp.Path, "Directory.Build.props"), "<Project />");
        Assert.IsTrue(signal.Wait(TestTime.Seconds(5)));
        Assert.IsTrue(monitor.PendingChanges.Any(change => change.Kind == PluginSourceChangeKind.BuildFilesChanged));
        monitor.MarkProcessed(null, root);
        Assert.AreEqual(0, monitor.PendingChanges.Count);
        signal.Reset();

        File.WriteAllText(Path.Combine(packageDirectory, "plugin3.cs"), "// changed");
        Assert.IsTrue(signal.Wait(TestTime.Seconds(5)));
        monitor.ClearPending();
        Assert.AreEqual(0, monitor.PendingChanges.Count);
    }

    [TestMethod]
    public async Task ChangeOperationCoordinatorSerializesOperationsAndMarksPendingOnSuccess()
    {
        var monitor = new FakeChangeMonitor();
        var coordinator = new PluginChangeOperationCoordinator();
        var active = 0;
        var maxActive = 0;

        await Task.WhenAll(
            coordinator.RunAndMarkProcessedAsync(monitor, "one", async _ =>
            {
                maxActive = Math.Max(maxActive, Interlocked.Increment(ref active));
                await Task.Delay(25);
                Interlocked.Decrement(ref active);
            }),
            coordinator.RunAndMarkProcessedAsync(monitor, "two", async _ =>
            {
                maxActive = Math.Max(maxActive, Interlocked.Increment(ref active));
                await Task.Delay(25);
                Interlocked.Decrement(ref active);
            }));

        Assert.AreEqual(1, maxActive);
        CollectionAssert.AreEquivalent(new[] { "one", "two" }, monitor.ProcessedPackageIds.ToArray());
    }

    private sealed class FakeChangeMonitor : IPluginChangeMonitor
    {
        public event EventHandler<PluginSourceChange>? Changed;

        public IReadOnlyList<PluginSourceChange> PendingChanges => [];

        public List<string?> ProcessedPackageIds { get; } = [];

        public void Start()
        {
            _ = Changed;
        }

        public void MarkProcessed(string? packageId, PluginRoot? root = null)
            => ProcessedPackageIds.Add(packageId);

        public void ClearPending()
        {
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }
}
