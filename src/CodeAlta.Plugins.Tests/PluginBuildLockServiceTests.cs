namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginBuildLockServiceTests
{
    [TestMethod]
    public async Task AcquireAsyncSerializesSamePackageLock()
    {
        using var temp = new TestTempDirectory();
        var package = CreatePackage(temp.Path, "hello");
        var service = new PluginBuildLockService(Path.Combine(temp.Path, ".cache"));
        var first = await service.AcquireAsync(package);
        var acquiredSecond = false;
        await using var acquiredSignal = new AsyncDisposableHolder(first);

        var secondTask = Task.Run(async () =>
        {
            await using var second = await service.AcquireAsync(package);
            acquiredSecond = true;
        });

        await Task.Delay(200);
        Assert.IsFalse(acquiredSecond);
        await acquiredSignal.DisposeAsync();
        await secondTask.WithinAsync(TestTime.Seconds(5));
        Assert.IsTrue(acquiredSecond);
    }

    [TestMethod]
    public void GetLockPathIsDeterministicAndPackageScoped()
    {
        using var temp = new TestTempDirectory();
        var service = new PluginBuildLockService(Path.Combine(temp.Path, ".cache"));
        var package = CreatePackage(temp.Path, "hello/world");

        var first = service.GetLockPath(package);
        var second = service.GetLockPath(package);

        Assert.AreEqual(first, second);
        StringAssert.EndsWith(first, "hello_world.lock");
    }

    private static SourcePluginPackage CreatePackage(string rootPath, string id)
    {
        var directoryName = id.Replace('/', '_');
        var directory = Path.Combine(rootPath, directoryName);
        Directory.CreateDirectory(directory);
        var entry = Path.Combine(directory, "plugin.cs");
        File.WriteAllText(entry, "// plugin");
        return new SourcePluginPackage
        {
            PackageId = id,
            PackageDirectory = directory,
            EntryFilePath = entry,
            Root = new PluginRoot { RootPath = rootPath, Scope = CodeAlta.Plugins.Abstractions.PluginScope.Global },
        };
    }

    private sealed class AsyncDisposableHolder : IAsyncDisposable
    {
        private IAsyncDisposable? _inner;

        public AsyncDisposableHolder(IAsyncDisposable inner)
            => _inner = inner;

        public async ValueTask DisposeAsync()
        {
            var inner = Interlocked.Exchange(ref _inner, null);
            if (inner is not null)
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
