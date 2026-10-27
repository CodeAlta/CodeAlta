using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Tests;

public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task WorkflowCancel_StopsARunOfAnotherSessionOfItsSpace_AndItsSessionIsToldWhoStoppedIt()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var providerId = new ModelProviderId("stop-model");
        var runtime = CreateRuntime(options, new StatefulProviderRuntime(providerId));
        await using var _ = runtime.ConfigureAwait(false);
        SessionExecutionOptions Execution() => new()
        {
            ProviderId = providerId,
            ProviderKey = providerId.Value,
            WorkingDirectory = root.Path,
            ProjectRoots = [],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };
        var owner = await runtime.CreateGlobalSessionAsync(Execution(), "Research").ConfigureAwait(false);
        var other = await runtime.CreateGlobalSessionAsync(Execution(), "Watcher").ConfigureAwait(false);
        await using var runs = new CodeModeRunRegistry(null);
        // A run that would research for days: nothing but a stop ends it.
        var run = runs.TryStart(owner.SessionId, "Research over days", 1000, "call-1", async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new CodeModeRunResult(CodeModeRunStatus.Completed, TimeSpan.Zero);
        }, out var refusal);
        Assert.IsNotNull(run, refusal);
        var dispatcher = CreateDispatcher(new AltaServiceCollection()
            .Add(options)
            .Add(new ProjectCatalog(options))
            .Add(new SessionViewCatalog(options))
            .Add(runtime)
            .Add(runs));

        var stopped = await dispatcher.InvokeAsync(["workflow", "cancel", run!.Id],
            caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = other.SessionId }).ConfigureAwait(false);
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        Assert.AreEqual(AltaExitCodes.Success, stopped.ExitCode, stopped.Transcript);
        Assert.AreEqual(CodeModeRunStatus.Cancelled, result.Status);
        StringAssert.Contains(result.Error?.Message, $"stopped by session {other.SessionId}", "its session learns who stopped it");
    }

    [TestMethod]
    public async Task StoppingABackgroundTaskOfASession_StopsItsScript_AsTheUserDid()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var runtime = CreateRuntime(options, new ModelProviderId("stop-task-model"));
        await using var _ = runtime.ConfigureAwait(false);
        await using var runs = new CodeModeRunRegistry(null);
        runtime.ScriptRuns = runs;
        var run = runs.TryStart("session-1", "Long research", 1000, "call-1", async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new CodeModeRunResult(CodeModeRunStatus.Completed, TimeSpan.Zero);
        }, out var refusal);
        Assert.IsNotNull(run, refusal);

        // The list above the prompt names a script by its run id: its Stop reaches the script, not the provider.
        Assert.IsFalse(await runtime.StopBackgroundTaskAsync("session-2", run!.Id).ConfigureAwait(false), "a script is stopped as a task of its own session only");
        Assert.IsTrue(await runtime.StopBackgroundTaskAsync("session-1", run.Id).ConfigureAwait(false));
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        Assert.AreEqual(CodeModeRunStatus.Cancelled, result.Status);
        StringAssert.Contains(result.Error?.Message, "stopped by the user");
    }
}
