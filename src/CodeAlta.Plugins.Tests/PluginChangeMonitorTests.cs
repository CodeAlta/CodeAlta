using System.Collections.Concurrent;
using System.Diagnostics;
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
        var published = new ConcurrentQueue<PluginSourceChange>();
        monitor.Changed += (_, change) => published.Enqueue(change);

        monitor.Start();
        File.WriteAllText(Path.Combine(packageDirectory, "plugin.cs"), "// changed");

        await PublishedAsync(published, static change => change.PackageId == "hello");
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
        var published = new ConcurrentQueue<PluginSourceChange>();
        monitor.Changed += (_, change) => published.Enqueue(change);

        monitor.Start();
        File.Move(source, Path.Combine(packageDirectory, "plugin2.cs"));

        await PublishedAsync(published, static change => change.PackageId == "hello");
        Assert.IsTrue(monitor.PendingChanges.Any(change => change.PackageId == "hello"));
        monitor.MarkProcessed("hello");
        Assert.IsFalse(monitor.PendingChanges.Any(change => change.PackageId == "hello"));

        File.WriteAllText(Path.Combine(temp.Path, "Directory.Build.props"), "<Project />");
        await PublishedAsync(published, static change => change.Kind == PluginSourceChangeKind.BuildFilesChanged);
        Assert.IsTrue(monitor.PendingChanges.Any(change => change.Kind == PluginSourceChangeKind.BuildFilesChanged));
        monitor.MarkProcessed(null, root);
        Assert.AreEqual(0, monitor.PendingChanges.Count);

        File.WriteAllText(Path.Combine(packageDirectory, "plugin3.cs"), "// changed");
        await PublishedAsync(published, static change => change.Path?.EndsWith("plugin3.cs", StringComparison.Ordinal) == true);
        monitor.ClearPending();
        Assert.AreEqual(0, monitor.PendingChanges.Count);
    }

    // Waits for the change a step made. A change in a package is published twice, for the package and for its folder (a
    // change of the root), in either order: the next change published is not always the one of the file just written.
    private static async Task PublishedAsync(ConcurrentQueue<PluginSourceChange> published, Func<PluginSourceChange, bool> expected)
    {
        var limit = TestTime.Seconds(5);
        var waited = Stopwatch.StartNew();
        while (!published.Any(expected))
        {
            if (waited.Elapsed > limit)
                Assert.Fail($"The change was not published in time. Published: {string.Join(", ", published.Select(static change => $"{change.Kind} {change.Path}"))}");
            await Task.Delay(10);
        }
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
