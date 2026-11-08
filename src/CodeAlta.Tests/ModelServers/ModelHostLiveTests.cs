using CodeAlta.ModelServers;

namespace CodeAlta.Tests.ModelServers;

/// <summary>
/// The model host on this machine, with the network: a real build of llama.cpp from GitHub, a small model from Hugging
/// Face, a router that serves it and answers. Run only when <c>CODEALTA_TEST_LLAMA_LIVE=1</c>; <c>CODEALTA_TEST_LLAMA_BACKEND</c>
/// names the backend (vulkan by default).
/// </summary>
[TestClass]
public sealed class ModelHostLiveTests
{
    [TestMethod]
    [Timeout(900_000, CooperativeCancellation = true)]
    public async Task InstallsDownloadsServesAndRemoves()
    {
        if (Environment.GetEnvironmentVariable("CODEALTA_TEST_LLAMA_LIVE") != "1") Assert.Inconclusive("Set CODEALTA_TEST_LLAMA_LIVE=1 to run it.");
        var backend = Environment.GetEnvironmentVariable("CODEALTA_TEST_LLAMA_BACKEND") is { Length: > 0 } named ? named : "vulkan";
        using var temp = TestTempDirectory.Create();
        await using var host = new ModelHost(new ModelHostOptions
        {
            SharedRoot = Path.Combine(temp.Path, "shared"),
            InstanceRoot = Path.Combine(temp.Path, "instance"),
            FirstPort = 19931,
        });
        await host.StartAsync(CancellationToken.None);
        host.SetSettings(host.Settings with { ModelsFolder = Path.Combine(temp.Path, "hub") });

        var install = host.InstallBuilds([backend], null, "test");
        await WaitAsync(() => host.GetStatus().Tasks.Single(task => task.Id == install.Id) is { EndedAt: not null }, TimeSpan.FromMinutes(10));
        var installed = host.GetStatus().Tasks.Single(task => task.Id == install.Id);
        Assert.AreEqual("done", installed.State, installed.Message);
        var build = host.Builds.Find(backend, null);
        Assert.IsNotNull(build);
        Assert.IsNotEmpty(build.Devices, "the probe saw the devices of the build");

        const string model = "bartowski/SmolLM2-135M-Instruct-GGUF:Q4_K_M";
        var download = host.Download(model, "test");
        await WaitAsync(() => host.GetStatus().Downloads.Single(item => item.Id == download.Id).State is "done" or "failed", TimeSpan.FromMinutes(5));
        var downloaded = host.GetStatus().Downloads.Single(item => item.Id == download.Id);
        Assert.AreEqual("done", downloaded.State, downloaded.Error);
        Assert.AreEqual(105_454_432L, host.GetStatus().Models.Single().SizeBytes);

        var server = await host.SetServerAsync(new ServerDefinition { Id = "", Name = "Live test", Backend = backend, Models = [new ServerModel { Model = model }] }, CancellationToken.None);
        var test = await host.TestServerAsync(server.Definition.Id, null, CancellationToken.None);
        Assert.IsTrue(test.Passed, test.Message);
        await WaitAsync(() => host.GetStatus().Servers.Single().Status.Models.Single().State == "loaded", TimeSpan.FromSeconds(30));
        var freed = await host.RemoveModelAsync(model, stopServers: true, CancellationToken.None);
        Assert.AreEqual(105_454_432L, freed);
        await host.RemoveServerAsync(server.Definition.Id, CancellationToken.None);
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > end) Assert.Fail("It did not happen in time.");
            await Task.Delay(500);
        }
    }
}
