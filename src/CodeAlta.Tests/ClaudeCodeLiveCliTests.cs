using CodeAlta.Agent;
using CodeAlta.Agent.Claude;

namespace CodeAlta.Tests;

/// <summary>
/// Runs the provider against a real Claude Code executable. The tests are skipped unless
/// <c>CODEALTA_TEST_CLAUDE_CLI</c> names one; the one that runs a model turn also needs
/// <c>CODEALTA_TEST_CLAUDE_TURN=1</c>, because it uses the account the CLI is signed in to.
/// </summary>
[TestClass]
public sealed class ClaudeCodeLiveCliTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [TestMethod]
    public async Task Probe_ListsTheModelsOfTheInstalledCli()
    {
        var command = RequireCli();
        await using var runtime = new ClaudeCodeModelProviderRuntime(CreateOptions(command));

        var probe = await runtime.ProbeAsync().WaitAsync(Timeout);

        Console.WriteLine($"{probe.Availability}: {probe.StatusMessage}");
        Console.WriteLine(string.Join(", ", probe.Models.Select(static model => $"{model.Id} ({model.DisplayName})")));
        Assert.IsTrue(probe.Models.Count > 0, "The CLI lists its models whether it is signed in or not.");
        Assert.IsTrue(probe.Availability is ModelProviderAvailability.Ready or ModelProviderAvailability.Failed);
        if (probe.Availability is ModelProviderAvailability.Failed)
        {
            StringAssert.Contains(probe.StatusMessage, "not signed in");
        }
    }

    [TestMethod]
    public async Task Turn_AnswersOrSaysHowToSignIn()
    {
        var command = RequireCli();
        if (Environment.GetEnvironmentVariable("CODEALTA_TEST_CLAUDE_TURN") != "1")
        {
            Assert.Inconclusive("Set CODEALTA_TEST_CLAUDE_TURN=1 to run a turn with the account of the CLI.");
        }

        using var directory = TestTempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "note.txt"), "The password of the test is 'marmalade'.\n");
        await using var runtime = new ClaudeCodeModelProviderRuntime(CreateOptions(command));
        var probe = await runtime.ProbeAsync().WaitAsync(Timeout);
        await using var session = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "claude-code",
            WorkingDirectory = directory.Path,
            Model = "haiku",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });
        var events = new List<AgentEvent>();
        session.Subscribe(@event =>
        {
            lock (events)
            {
                events.Add(@event);
            }
        });

        var send = session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Read note.txt and answer with the password it gives, in one word.") });
        if (probe.Availability is not ModelProviderAvailability.Ready)
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => send.WaitAsync(Timeout));
            Console.WriteLine(failure.Message);
            StringAssert.Contains(failure.Message, "not signed in");
            return;
        }

        await send.WaitAsync(Timeout);
        AgentEvent[] snapshot;
        lock (events)
        {
            snapshot = [.. events];
        }

        foreach (var @event in snapshot.Where(static e => e is not AgentContentDeltaEvent))
        {
            Console.WriteLine(@event switch
            {
                AgentActivityEvent activity => $"activity {activity.Phase} {activity.Name}",
                AgentContentCompletedEvent content => $"content {content.Kind}: {content.Content}",
                AgentSessionUpdateEvent update => $"update {update.Kind} {update.Message} window={update.Usage?.Window?.CurrentTokens}/{update.Usage?.Window?.TokenLimit}",
                _ => @event.GetType().Name,
            });
        }

        var answer = snapshot.OfType<AgentContentCompletedEvent>().Last(static e => e.Kind == AgentContentKind.Assistant).Content;
        StringAssert.Contains(answer.ToLowerInvariant(), "marmalade");
        Assert.IsTrue(snapshot.OfType<AgentActivityEvent>().Any(static e => e is { Name: "Read", Phase: AgentActivityPhase.Completed }));
    }

    private static string RequireCli()
    {
        var command = Environment.GetEnvironmentVariable("CODEALTA_TEST_CLAUDE_CLI");
        if (string.IsNullOrWhiteSpace(command))
        {
            Assert.Inconclusive("Set CODEALTA_TEST_CLAUDE_CLI to the path of a Claude Code executable to run this test.");
        }

        return command!;
    }

    private static ClaudeCodeModelProviderRuntimeOptions CreateOptions(string command)
        => new() { ProviderKey = "claude-code", DisplayName = "Claude Code", Command = command, IdleTimeout = TimeSpan.Zero };
}
