using CodeAlta.Desktop.Speech;
using CodeAlta.NativeEngines;
using static CodeAlta.Desktop.Tests.SpeechTestSupport;

namespace CodeAlta.Desktop.Tests;

/// <summary>The native libraries speech, search by meaning and the Pseudonymizer share: where they are, and their one download.</summary>
[TestClass]
public sealed class SherpaRuntimeTests
{
    [TestMethod]
    public void WhatSpeechDownloadedBefore_IsMovedIntoTheSharedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = SpeechModelCatalog.FindEngine("osx-arm64")!;
            var speech = Path.Combine(root, "cache", "speech");
            var runtimeRoot = Path.Combine(root, "cache", "runtime");
            var runtime = new SherpaRuntime(runtimeRoot, speech, engine, handler: null);
            var shared = SherpaRuntime.DirectoryOf(runtimeRoot, engine);

            Assert.AreEqual(shared, runtime.InstallDirectory, "with nothing installed, the shared folder is where it will go");
            Assert.IsFalse(runtime.IsInstalled);
            Assert.IsNull(runtime.OnnxRuntimeLibrary());

            var legacy = SherpaRuntime.LegacyDirectoryOf(speech, engine);
            Directory.CreateDirectory(legacy);
            foreach (var library in engine.Libraries) File.WriteAllText(Path.Combine(legacy, library), "lib");
            File.WriteAllText(Path.Combine(legacy, SherpaRuntime.Marker), "{}");

            Assert.AreEqual(shared, runtime.InstallDirectory);
            Assert.IsTrue(runtime.IsInstalled);
            Assert.IsFalse(Directory.Exists(legacy));
            Assert.AreEqual(Path.Combine(shared, "libonnxruntime.dylib"), runtime.OnnxRuntimeLibrary());
            Assert.AreEqual(shared, runtime.InstallDirectory, "once moved, it stays where it is");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OneDownload_ServesAllWhoAskWhileItRuns()
    {
        using var fixture = new Fixture();
        var runtime = fixture.Create();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Hold[fixture.Engine.Address.AbsoluteUri] = held.Task;
        Counter speech = new(), search = new();

        // Speech, search and the Pseudonymizer ask within the same seconds, as after an update that changes the version.
        var first = runtime.InstallAsync(speech, CancellationToken.None);
        await Until(() => fixture.Requests == 1, "the download to start");
        var second = runtime.InstallAsync(search, CancellationToken.None);
        using var leaving = new CancellationTokenSource();
        var third = runtime.InstallAsync(progress: null, leaving.Token);
        leaving.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => third);
        Assert.IsFalse(first.IsCompleted || second.IsCompleted, "who stops waiting does not stop the download of the others");

        held.SetResult();
        await Task.WhenAll(first, second);
        Assert.AreEqual(1, fixture.Requests, "the package is downloaded once");
        Assert.AreEqual((fixture.Engine.Size, fixture.Engine.Size), (speech.Bytes, search.Bytes), "each is told the whole download");
        Assert.IsTrue(runtime.IsInstalled);
        Assert.AreEqual("runtime", File.ReadAllText(runtime.OnnxRuntimeLibrary()!));

        // An engine that is there is not downloaded again, and its bytes are told as on disk.
        var later = new Counter();
        await runtime.InstallAsync(later, CancellationToken.None);
        Assert.AreEqual((1, fixture.Engine.Size), (fixture.Requests, later.Bytes));
        await runtime.CloseAsync();
    }

    [TestMethod]
    public async Task ADownloadThatFails_FailsAllWhoWait_AndTheNextOneTriesAgain()
    {
        using var fixture = new Fixture();
        var runtime = fixture.Create();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var address = fixture.Engine.Address.AbsoluteUri;
        fixture.Http.Hold[address] = held.Task;
        fixture.Http.Content.Remove(address, out var package);

        var first = runtime.InstallAsync(progress: null, CancellationToken.None);
        await Until(() => fixture.Requests == 1, "the download to start");
        var second = runtime.InstallAsync(progress: null, CancellationToken.None);
        held.SetResult();
        Assert.AreEqual("not_found", (await Assert.ThrowsExactlyAsync<PinnedDownloadException>(() => first)).Code);
        Assert.AreEqual("not_found", (await Assert.ThrowsExactlyAsync<PinnedDownloadException>(() => second)).Code);
        Assert.IsFalse(runtime.IsInstalled);

        fixture.Http.Content[address] = package!;
        await runtime.InstallAsync(progress: null, CancellationToken.None);
        Assert.IsTrue(runtime.IsInstalled);
        Assert.AreEqual(2, fixture.Requests);
        await runtime.CloseAsync();

        var nowhere = new SherpaRuntime(Path.Combine(fixture.Root, "runtime"), Path.Combine(fixture.Root, "speech"), engine: null, fixture.Http);
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => nowhere.InstallAsync(progress: null, CancellationToken.None));
        Assert.IsFalse(nowhere.IsInstalled);
        Assert.IsNull(nowhere.OnnxRuntimeLibrary());
    }

    private sealed class Counter : IPinnedDownloadProgress
    {
        private long _bytes;

        public long Bytes => Interlocked.Read(ref _bytes);

        public void Received(long bytes) => Interlocked.Add(ref _bytes, bytes);

        public void Checking(PinnedFile file)
        {
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-runtime-" + Guid.NewGuid().ToString("N"))).FullName;
            (Engine, var package) = EnginePackage(("libonnxruntime.test", "runtime"));
            Http.Content[Engine.Address.AbsoluteUri] = package;
        }

        public string Root { get; }
        public SpeechEngineDefinition Engine { get; }
        public FakeDownloadServer Http { get; } = new();

        /// <summary>Gets how many times the package was asked for.</summary>
        public int Requests { get { lock (Http.Requests) return Http.Requests.Count; } }

        public SherpaRuntime Create() => new(Path.Combine(Root, "runtime"), Path.Combine(Root, "speech"), Engine, Http);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
