using System.Net;
using CodeAlta.ModelServers;

namespace CodeAlta.Tests.ModelServers;

[TestClass]
public sealed class ModelHostKeepAwakeTests
{
    [TestMethod]
    public async Task TheMachineIsKeptAwakeWhileItsModelHostWorks_AndFiveMinutesAfter()
    {
        using var temp = TestTempDirectory.Create();
        var time = new SteppedTime(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var released = 0;
        await using var host = new ModelHost(new ModelHostOptions
        {
            SharedRoot = Path.Combine(temp.Path, "shared"), InstanceRoot = Path.Combine(temp.Path, "instance"), Time = time,
            // The install asks GitHub for its release, and waits for the answer: its task runs meanwhile.
            NetworkHandler = new WaitingHandler(answer.Task),
            KeepAwake = reason =>
            {
                StringAssert.Contains(reason, "model servers");
                held++;
                return new Release(() => released++);
            },
        });

        host.HoldAwake();
        Assert.AreEqual(0, held, "an idle machine sleeps as its system decides");

        host.InstallBuilds(["cpu"], null, "test");
        host.HoldAwake();
        host.HoldAwake();
        Assert.AreEqual(1, held, "one assertion while it works");
        Assert.IsTrue(host.IsKeptAwake);

        answer.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        for (var i = 0; i < 200 && host.GetStatus().Tasks.Any(static task => task.EndedAt is null); i++) await Task.Delay(25);
        Assert.IsFalse(host.GetStatus().Tasks.Any(static task => task.EndedAt is null), "the install ended");
        host.HoldAwake();
        time.Advance(TimeSpan.FromMinutes(4));
        host.HoldAwake();
        Assert.AreEqual(0, released, "a request that comes soon after finds the machine awake");
        time.Advance(TimeSpan.FromMinutes(2));
        host.HoldAwake();
        Assert.AreEqual(1, released);
        Assert.IsFalse(host.IsKeptAwake);
    }

    [TestMethod]
    public async Task ASystemThatRefusesIsNotAskedAgain()
    {
        using var temp = TestTempDirectory.Create();
        var asked = 0;
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new ModelHost(new ModelHostOptions
        {
            SharedRoot = Path.Combine(temp.Path, "shared"), InstanceRoot = Path.Combine(temp.Path, "instance"), NetworkHandler = new WaitingHandler(answer.Task),
            KeepAwake = _ =>
            {
                asked++;
                throw new InvalidOperationException("systemd-inhibit is unavailable.");
            },
        });

        host.InstallBuilds(["cpu"], null, "test");
        host.HoldAwake();
        host.HoldAwake();
        Assert.AreEqual(1, asked);
        Assert.IsFalse(host.IsKeptAwake);
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    [TestMethod]
    public async Task GitHubFailingAtTheStart_LeavesTheHostRunning_AndItsCloseWhole()
    {
        using var temp = TestTempDirectory.Create();
        var asked = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var host = new ModelHost(new ModelHostOptions
        {
            SharedRoot = Path.Combine(temp.Path, "shared"), InstanceRoot = Path.Combine(temp.Path, "instance"),
            // The versioned release is there, but its nightly-tag.txt answers 504, as GitHub did on 2026-11-09.
            NetworkHandler = new RecordingHandler(asked, gatewayTimeout: "/nightly-tag.txt"),
        });
        Assert.IsNull(await host.CheckLatestAsync(force: true, CancellationToken.None), "a build GitHub could not give is no build, not a failure");
        await host.StartAsync(CancellationToken.None);
        for (var i = 0; i < 200 && !asked.Contains("/nightly-tag.txt"); i++) await Task.Delay(25);
        await Task.Delay(200);
        // The loop of the host is alive (its updates, its keep-awake) and its close goes through to the end: its servers stop.
        await host.DisposeAsync();
    }

    [TestMethod]
    public void ARollback_LeavesNoWayBackToTheBuildThatFailed()
    {
        using var temp = TestTempDirectory.Create();
        using var http = new HttpClient();
        var builds = new CodeAlta.ModelServers.LlamaCpp.LlamaBuilds(Path.Combine(temp.Path, "builds"), http);
        builds.MakeCurrent("rocm", "b12110");
        builds.MakeCurrent("rocm", "b12193");
        Assert.AreEqual("b12110", builds.Read().Previous["rocm"], "an update keeps the build it leaves as the way back");
        builds.MakeCurrent("rocm", "b8200");
        builds.MarkBad("rocm", "b8200");
        builds.MakeCurrent("rocm", "b12193");
        var state = builds.Read();
        Assert.AreEqual("b12193", state.Current["rocm"]);
        Assert.IsFalse(state.Previous.ContainsKey("rocm"), "the build that failed its test is not offered back");
        Assert.IsTrue(builds.IsBad("rocm", "b8200"));
    }

    [TestMethod]
    public async Task APolicyThatAllowsLemonade_ReadsItsNewestBuildAtOnce()
    {
        using var temp = TestTempDirectory.Create();
        var asked = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var host = new ModelHost(new ModelHostOptions
        {
            SharedRoot = Path.Combine(temp.Path, "shared"), InstanceRoot = Path.Combine(temp.Path, "instance"),
            NetworkHandler = new RecordingHandler(asked),
        });
        host.SetSettings(host.Settings with { Updates = host.Settings.Updates with { AllowLemonadeRocm = true } });
        for (var i = 0; i < 200 && !asked.Any(static path => path.Contains("lemonade-sdk/llamacpp-rocm", StringComparison.Ordinal)); i++) await Task.Delay(25);
        Assert.IsTrue(asked.Any(static path => path.EndsWith("/repos/lemonade-sdk/llamacpp-rocm/releases/latest", StringComparison.Ordinal)), string.Join(", ", asked));
    }

    // Answers the GitHub API: a stable release that names b12110, that build, and Lemonade's newest; records what is asked.
    private sealed class RecordingHandler(System.Collections.Concurrent.ConcurrentQueue<string> asked, string? gatewayTimeout = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            asked.Enqueue(path);
            if (path == gatewayTimeout) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
            var json = path switch
            {
                "/repos/ggml-org/llama.cpp/releases/latest" => """{ "tag_name": "v0.9.0", "assets": [ { "name": "nightly-tag.txt", "browser_download_url": "https://github.example/nightly-tag.txt", "size": 7 } ] }""",
                "/nightly-tag.txt" => "b12110",
                "/repos/ggml-org/llama.cpp/releases/tags/b12110" => """{ "tag_name": "b12110", "published_at": "2026-11-04T00:00:00Z", "assets": [ { "name": "llama-b12110-bin-win-vulkan-x64.zip", "browser_download_url": "https://x/v", "size": 1, "digest": "sha256:aa" } ] }""",
                "/repos/lemonade-sdk/llamacpp-rocm/releases/latest" => """{ "tag_name": "b1371", "published_at": "2026-11-08T00:00:00Z", "assets": [ { "name": "llama-b1371-windows-rocm-gfx110X-x64.zip", "browser_download_url": "https://x/l", "size": 1, "digest": "sha256:bb" } ] }""",
                _ => null,
            };
            return Task.FromResult(json is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class SteppedTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class WaitingHandler(Task<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => await answer.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Release(Action released) : IDisposable
    {
        public void Dispose() => released();
    }
}
