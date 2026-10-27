using System.Collections.Concurrent;
using System.Text.Json;
using CodeAlta.CodeMode;

namespace CodeAlta.CodeMode.Tests;

/// <summary><c>ask()</c> in the engine, over a handler of <c>codemode.ask</c> that plays the user.</summary>
[TestClass]
public sealed class CodeModeAskTests
{
    private static readonly CodeModeApi Api = new(
    [
        new CodeModeFunction("codemode.ask") { WaitsForUser = true },
        new CodeModeFunction("tools.wait"),
    ]);

    // The user answers "yes" to a question that ends with "?", dismisses one that starts with "Stop", and takes the
    // time the test gives; tools.wait takes that time too, without waiting for the user.
    private static DelegateHandler User(ConcurrentQueue<string> asked, int delayMilliseconds = 0) => new DelegateHandler()
        .On("codemode.ask", async (call, token) =>
        {
            var question = JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("question").GetString()!;
            asked.Enqueue(question);
            await Task.Delay(delayMilliseconds, token);
            return question.StartsWith("Stop", StringComparison.Ordinal)
                ? CodeModeCallResult.Failure(new CodeModeCallError("AskError", "The user dismissed the question.") { Code = "cancelled" })
                : CodeModeCallResult.Success("""{"answer":"yes"}""");
        })
        .On("tools.wait", async (_, token) =>
        {
            await Task.Delay(delayMilliseconds, token);
            return CodeModeCallResult.Text("done");
        });

    private static Task<CodeModeRunResult> RunAsync(string code, DelegateHandler handler, CodeModeRunOptions? options = null)
        => new CodeModeHost(Api, new CodeModeScriptCache()).RunAsync(CodeModeSource.Prepare(code), handler, options);

    [TestMethod]
    public async Task AnAnswer_ResolvesAsk_AndADismissalRejectsWithItsCode()
    {
        var asked = new ConcurrentQueue<string>();

        var result = await RunAsync("""
            const answer = await ask("Go?", { choices: ["yes", "no"] });
            let dismissed;
            try { await ask("Stop now"); } catch (e) { dismissed = `${e.name}:${e.code}`; }
            const invalid = await ask("").then(() => "asked", (e) => e.name);
            return [answer, dismissed, invalid];
            """, User(asked));

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("""["yes","AskError:cancelled","TypeError"]""", result.ValueJson);
        CollectionAssert.AreEqual(new[] { "Go?", "Stop now" }, asked.ToArray());
    }

    [TestMethod]
    public async Task TheTimeLimit_DoesNotCountTheTimeTheUserTakes_AlsoInAScriptWorkflowStarts()
    {
        var limits = CodeModeLimits.Default with { Timeout = TimeSpan.FromMilliseconds(400) };
        var options = new CodeModeRunOptions
        {
            Limits = limits,
            ResolveScript = (_, _) => ValueTask.FromResult(CodeModeSource.Prepare("""return await ask("Nested?");""", "nested.js")),
        };

        var answered = await RunAsync("""return [await ask("Slow?"), await workflow({ scriptPath: "nested.js" })];""", User(new(), 900), options);
        var computed = await RunAsync("""return await tools.wait({});""", User(new(), 900), options);

        Assert.AreEqual(CodeModeRunStatus.Completed, answered.Status, answered.Error?.Message);
        Assert.AreEqual("""["yes","yes"]""", answered.ValueJson);
        Assert.AreEqual(CodeModeErrorKind.Timeout, computed.Error?.Kind, "a call that does not wait for the user counts");
    }

    [TestMethod]
    public async Task AReplayedStep_DoesNotAskAgain_AndAnAskOutsideAStepAsksAgain()
    {
        const string code = """
            const outside = await ask("Outside?");
            const inside = await step("confirm", () => ask("Inside?"));
            return [outside, inside];
            """;
        var first = await RunAsync(code, User(new()));
        var recorded = first.Steps.Select(static step => new CodeModeReplayStep(step.Index, step.Key!, step.Succeeded) { HasResult = step.HasResult, ResultJson = step.ResultJson });
        var asked = new ConcurrentQueue<string>();

        var resumed = await RunAsync(code, User(asked), new CodeModeRunOptions { Replay = new CodeModeReplay(recorded, CodeModeReplayMode.Resume) });

        Assert.AreEqual(first.ValueJson, resumed.ValueJson);
        CollectionAssert.AreEqual(new[] { "Outside?" }, asked.ToArray(), "the step gives its recorded answer; the question outside it is asked again");
    }
}
