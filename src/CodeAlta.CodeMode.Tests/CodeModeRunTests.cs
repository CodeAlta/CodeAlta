using System.Diagnostics;
using System.Text.Json;
using CodeAlta.CodeMode;

namespace CodeAlta.CodeMode.Tests;

[TestClass]
public sealed class CodeModeRunTests
{
    private static readonly CodeModeApi Api = new(
    [
        new CodeModeFunction("tools.echo"),
        new CodeModeFunction("tools.slow"),
        new CodeModeFunction("tools.fail"),
        new CodeModeFunction("tools.write") { Parallel = false },
        new CodeModeFunction("alta.session.list"),
    ]);

    private static async Task<CodeModeRunResult> RunAsync(string code, DelegateHandler? handler = null, CodeModeRunOptions? options = null, CancellationToken cancellationToken = default, ICodeModeRunObserver? observer = null)
    {
        var host = new CodeModeHost(Api, new CodeModeScriptCache());
        return await host.RunAsync(CodeModeSource.Prepare(code), handler ?? Handler(), options, observer, cancellationToken);
    }

    private static DelegateHandler Handler(int slowMilliseconds = 300) => new DelegateHandler()
        .On("tools.echo", args => args)
        .On("alta.session.list", _ => new Dictionary<string, object> { ["count"] = 2 })
        .On("tools.slow", async (_, cancellationToken) =>
        {
            await Task.Delay(slowMilliseconds, cancellationToken);
            return CodeModeCallResult.Text("slow");
        })
        .On("tools.fail", (_, _) => throw new InvalidOperationException("host bug"))
        .On("tools.write", async (call, cancellationToken) =>
        {
            await Task.Delay(50, cancellationToken);
            return CodeModeCallResult.Text(call.ArgumentsJson);
        });

    [TestMethod]
    public async Task ReturnedValue_IsTheResult()
    {
        var result = await RunAsync("const x = 20;\nreturn x + 22;");

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("42", result.ValueJson);
    }

    [TestMethod]
    public async Task AGuessedFunction_FailsWithWhatItsNamespaceHas()
    {
        var result = await RunAsync("""
            const errors = [];
            for (const call of [() => tools.read_file({ path: "a" }), () => mcp.tiny.echo({}), () => alta.sesion.list({})]) {
              try { await call(); } catch (e) { errors.push(`${e.name}: ${e.message}`); }
            }
            return { errors, mcp: typeof mcp, echo: await tools.echo({ text: "still works" }) };
            """);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        using var value = JsonDocument.Parse(result.ValueJson!);
        var errors = value.RootElement.GetProperty("errors").EnumerateArray().Select(static error => error.GetString()!).ToArray();
        StringAssert.StartsWith(errors[0], "TypeError: tools.read_file is not a function of this script. tools has: echo, slow, fail, write.");
        StringAssert.Contains(errors[1], "mcp.tiny.echo is not a function", "mcp exists even without an MCP tool");
        StringAssert.Contains(errors[1], "from the next exec");
        StringAssert.Contains(errors[2], "alta has: session");
        Assert.AreEqual("object", value.RootElement.GetProperty("mcp").GetString());
        Assert.AreEqual("still works", value.RootElement.GetProperty("echo").GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task FinalExpression_IsReturned()
    {
        var result = await RunAsync("const items = [1, 2, 3];\nitems.map(x => x * 2)");

        Assert.AreEqual("[2,4,6]", result.ValueJson);
    }

    [TestMethod]
    public async Task Calls_ReachTheHandler_AndResolveWithTheirValue()
    {
        var result = await RunAsync("""
            const a = await tools.echo({ path: "src/app.ts", n: 1 });
            const s = await alta.session.list();
            return { path: a.path, count: s.count };
            """);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("""{"path":"src/app.ts","count":2}""", result.ValueJson);
        Assert.HasCount(2, result.Calls);
        Assert.AreEqual(new CodeModeSourceLocation(1, 17), result.Calls[0].Location);
    }

    [TestMethod]
    public async Task PromiseAll_RunsHostCallsAtOnce()
    {
        var watch = Stopwatch.StartNew();
        var result = await RunAsync("return await Promise.all([tools.slow(), tools.slow(), tools.slow()]);");

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("""["slow","slow","slow"]""", result.ValueJson);
        Assert.AreEqual(3, result.MaxCallsAtOnce);
        Assert.IsLessThan(800, watch.ElapsedMilliseconds, "three calls of 300 ms ran one after the other");
    }

    [TestMethod]
    public async Task FunctionsThatAreNotParallel_RunOneAtATime()
    {
        var result = await RunAsync("return await Promise.all([1, 2, 3].map(i => tools.write({ i })));");

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual(1, result.MaxCallsAtOnce);
    }

    [TestMethod]
    public async Task AnAwaitLongerThanJintsDefaultPromiseTimeout_Succeeds()
    {
        // Jint 4.17 fails any await longer than 10 s unless the run lifts its promise timeout.
        var result = await RunAsync("return await tools.slow();", Handler(slowMilliseconds: 11_000));

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("\"slow\"", result.ValueJson);
    }

    [TestMethod]
    public async Task Calls_BelongToTheirStep_AcrossAwaitsAndParallelBranches()
    {
        var handler = Handler(slowMilliseconds: 50);
        var result = await RunAsync("""
            async function inner(n) { await tools.echo({ n }); await null; return tools.echo({ n: n + 10 }); }
            await Promise.all([
              step("a", async () => { await tools.echo({ n: 1 }); await inner(2); }),
              step("b", async () => { await tools.slow(); await tools.echo({ n: 4 }); }),
            ]);
            await tools.echo({ n: 99 });
            """, handler);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        string? StepOf(int n) => handler.Calls.Single(call => call.Path == "tools.echo" && JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("n").GetInt32() == n).StepId;
        Assert.AreEqual("a", StepOf(1));
        Assert.AreEqual("a", StepOf(2));
        Assert.AreEqual("a", StepOf(12));
        Assert.AreEqual("b", StepOf(4));
        Assert.IsNull(StepOf(99));
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, result.Steps.Select(step => step.StepId).ToArray());
        Assert.IsTrue(result.Steps.All(step => step.Succeeded));
    }

    [TestMethod]
    public async Task AnExceptionOfTheHost_IsAnErrorTheScriptCanCatch()
    {
        var result = await RunAsync("""
            try { await tools.fail(); } catch (e) { return `${e.name}: ${e.message}`; }
            """);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("\"ToolError: host bug\"", result.ValueJson);
        Assert.AreEqual(CodeModeCallStatus.Failed, result.Calls.Single().Status);
    }

    [TestMethod]
    public async Task AFailedCall_GivesItsNameAndCode()
    {
        var handler = new DelegateHandler().On("tools.echo", (_, _) => Task.FromResult(CodeModeCallResult.Failure(new("AltaError", "no such session") { Code = "session.notFound" })));
        var result = await RunAsync("try { await tools.echo({}); } catch (e) { return [e.name, e.code, e instanceof Error]; }", handler);

        Assert.AreEqual("""["AltaError","session.notFound",true]""", result.ValueJson);
    }

    [TestMethod]
    public async Task TheBridge_CannotBePatched()
    {
        var result = await RunAsync("""
            const failures = [];
            try { tools = null; } catch (e) { failures.push(e.name); }
            try { tools.echo = null; } catch (e) { failures.push(e.name); }
            try { console.log = null; } catch (e) { failures.push(e.name); }
            return [failures, typeof __host];
            """);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("""[["TypeError","TypeError","TypeError"],"undefined"]""", result.ValueJson);
    }

    [TestMethod]
    public async Task TheEngine_ReachesNeitherNetNorFilesNorNetwork()
    {
        var result = await RunAsync("""
            return [typeof fetch, typeof require, typeof setTimeout, typeof process, typeof System, typeof importNamespace];
            """);

        Assert.AreEqual("""["undefined","undefined","undefined","undefined","undefined","undefined"]""", result.ValueJson);
    }

    [TestMethod]
    public async Task StringCompilation_IsOff()
    {
        var result = await RunAsync("""
            const out = [];
            try { eval("1 + 1"); out.push("eval ran"); } catch (e) { out.push(e.name); }
            try { new Function("return 1")(); out.push("Function ran"); } catch (e) { out.push(e.name); }
            return out;
            """);

        CollectionAssert.DoesNotContain(JsonSerializer.Deserialize(result.ValueJson!, TestJsonContext.Default.JsonElement).EnumerateArray().Select(e => e.GetString()).ToArray(), "eval ran");
    }

    [TestMethod]
    public async Task DeepRecursion_EndsTheRun_NotTheProcess()
    {
        var options = new CodeModeRunOptions { Limits = CodeModeLimits.Default with { MaxRecursionDepth = 10_000_000 } };
        var result = await RunAsync("function f(n) { return f(n + 1) + 1; }\nreturn f(0);", options: options);

        Assert.AreEqual(CodeModeRunStatus.Failed, result.Status);
        Assert.AreEqual(CodeModeErrorKind.Recursion, result.Error!.Kind, result.Error.Message);
    }

    [TestMethod]
    public async Task RecursionLimit_EndsTheRun()
    {
        var result = await RunAsync("function f(n) { return f(n + 1); }\nreturn f(0);");

        Assert.AreEqual(CodeModeErrorKind.Recursion, result.Error?.Kind);
    }

    [TestMethod]
    public async Task ABusyLoop_EndsWithTheCpuLimit_ButAwaitedTimeDoesNotCount()
    {
        var limits = CodeModeLimits.Default with { CpuTimePerSlice = TimeSpan.FromMilliseconds(300) };
        var busy = await RunAsync("for (;;) {}", options: new() { Limits = limits });
        var waiting = await RunAsync("await tools.slow(); return 1;", Handler(slowMilliseconds: 600), new() { Limits = limits });

        Assert.AreEqual(CodeModeErrorKind.CpuTime, busy.Error?.Kind);
        Assert.AreEqual(CodeModeRunStatus.Completed, waiting.Status, waiting.Error?.Message);

        // Work right after a long await is a new slice: the wait never counts, whatever statement the clock is read at.
        var resumed = await RunAsync("await tools.slow(); let s = 0; for (let i = 0; i < 200000; i++) s += i % 7; await tools.slow(); return s;",
            Handler(slowMilliseconds: 400), new() { Limits = limits });
        Assert.AreEqual(CodeModeRunStatus.Completed, resumed.Status, resumed.Error?.Message);
    }

    [TestMethod]
    public async Task StatementAndMemoryLimits_EndTheRun()
    {
        var statements = await RunAsync("let i = 0; for (;;) { i++; }", options: new() { Limits = CodeModeLimits.Default with { MaxStatements = 1_000 } });
        var memory = await RunAsync("const a = []; for (;;) a.push({ x: 'abc' + a.length });", options: new() { Limits = CodeModeLimits.Default with { MemoryBytes = 5_000_000 } });

        Assert.AreEqual(CodeModeErrorKind.Statements, statements.Error?.Kind);
        Assert.AreEqual(CodeModeErrorKind.Memory, memory.Error?.Kind);
    }

    [TestMethod]
    public async Task ASlowRegularExpression_EndsWithItsOwnMessage()
    {
        var result = await RunAsync("return /(a+)+$/.test('a'.repeat(40) + '!');", options: new() { Limits = CodeModeLimits.Default with { RegexTimeout = TimeSpan.FromMilliseconds(100) } });

        Assert.AreEqual(CodeModeErrorKind.Regex, result.Error?.Kind, result.Error?.Message);
    }

    [TestMethod]
    public async Task AScriptWaitingOnNothing_FailsAsStalled()
    {
        var watch = Stopwatch.StartNew();
        var result = await RunAsync("await new Promise(() => {});\nreturn 1;");

        Assert.AreEqual(CodeModeErrorKind.Stalled, result.Error?.Kind);
        Assert.IsLessThan(5_000, watch.ElapsedMilliseconds);
    }

    [TestMethod]
    public async Task CallsNotAwaited_AreCancelledAndListed()
    {
        var result = await RunAsync("tools.slow();\nreturn 'done';", Handler(slowMilliseconds: 10_000));
        var text = CodeModeResultFormatter.Format(result);

        Assert.AreEqual("\"done\"", result.ValueJson);
        Assert.AreEqual(CodeModeCallStatus.Cancelled, result.Calls.Single().Status);
        StringAssert.Contains(text, "Not awaited, cancelled when the script ended: tools.slow ⊘");
    }

    [TestMethod]
    public async Task Cancellation_StopsTheRunAndItsCalls()
    {
        using var cancellation = new CancellationTokenSource(300);
        var result = await RunAsync("return await tools.slow();", Handler(slowMilliseconds: 30_000), cancellationToken: cancellation.Token);

        Assert.AreEqual(CodeModeRunStatus.Cancelled, result.Status);
        Assert.AreEqual(CodeModeCallStatus.Cancelled, result.Calls.Single().Status);
    }

    [TestMethod]
    public async Task TheWallTime_EndsTheRun()
    {
        var result = await RunAsync("return await tools.slow();", Handler(slowMilliseconds: 30_000), new() { Limits = CodeModeLimits.Default with { Timeout = TimeSpan.FromMilliseconds(300) } });

        Assert.AreEqual(CodeModeErrorKind.Timeout, result.Error?.Kind);
    }

    [TestMethod]
    public async Task CallsBeyondTheLimit_AreRefused()
    {
        var result = await RunAsync("""
            let refused = 0;
            for (let i = 0; i < 5; i++) { try { await tools.echo({ i }); } catch (e) { if (e.code === "codemode.callLimit") refused++; } }
            return refused;
            """, options: new() { Limits = CodeModeLimits.Default with { MaxCalls = 3 } });

        Assert.AreEqual("2", result.ValueJson);
        Assert.AreEqual(2, result.Calls.Count(call => call.Status == CodeModeCallStatus.Refused));
    }

    [TestMethod]
    public async Task Output_IsKeptInOrder_AndBounded()
    {
        var observer = new RecordingObserver();
        var result = await RunAsync("""
            console.log("a", { b: 1 });
            text("for the model");
            console.warn("careful");
            for (let i = 0; i < 100; i++) log("x".repeat(100));
            """, options: new() { Limits = CodeModeLimits.Default with { MaxLogCharacters = 1_000 } }, observer: observer);

        Assert.AreEqual("a {\"b\":1}", result.Output[0].Text);
        Assert.AreEqual(CodeModeOutputKind.Text, result.Output[1].Kind);
        Assert.AreEqual(CodeModeOutputKind.Warning, result.Output[2].Kind);
        Assert.IsGreaterThan(0, result.DroppedOutputCharacters);
        Assert.IsTrue(observer.Events.Contains("output for the model"));
    }

    [TestMethod]
    public async Task ArgsClockAndRandom_ComeFromTheRun()
    {
        var options = new CodeModeRunOptions
        {
            ArgumentsJson = """{"pr":231}""",
            Clock = () => new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero),
            Random = () => 0.25,
        };
        var result = await RunAsync("return [args.pr, new Date().toISOString(), Date.now(), Math.random()];", options: options);

        Assert.AreEqual("""[231,"2026-10-26T12:00:00.000Z",1793016000000,0.25]""", result.ValueJson);
    }

    [TestMethod]
    public async Task ARuntimeError_PointsAtItsLine()
    {
        var result = await RunAsync("const x = {};\nconst y = 1;\nreturn x.a.b;");
        var text = CodeModeResultFormatter.Format(result);

        Assert.AreEqual(CodeModeErrorKind.Script, result.Error?.Kind);
        Assert.AreEqual("TypeError", result.Error!.Name);
        Assert.AreEqual(3, result.Error.Location?.Line);
        StringAssert.StartsWith(text, "Script failed at line 3:");
        StringAssert.Contains(text, "   3 | return x.a.b;");
    }

    [TestMethod]
    public async Task AnErrorOnTheFirstLine_HasTheColumnOfTheSource()
    {
        var result = await RunAsync("null.f();");

        Assert.AreEqual(1, result.Error?.Location?.Line);
        Assert.IsLessThanOrEqualTo("null.f();".Length, result.Error!.Location!.Value.Column);
    }

    [TestMethod]
    public async Task AFailure_ListsTheCallsAlreadyMade()
    {
        var result = await RunAsync("await tools.echo({});\nawait tools.echo({});\nthrow new Error('stop');");
        var text = CodeModeResultFormatter.Format(result);

        StringAssert.Contains(text, "Error: stop");
        StringAssert.Contains(text, "Calls made before the failure (not undone): tools.echo ✓ ×2");
    }

    [TestMethod]
    public async Task Workflow_RunsAnotherScriptWithItsArguments_AndGivesItsValueOrItsError()
    {
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["double.js"] = "log('doubling');\nreturn { n: args.n * 2, echoed: (await tools.echo({ v: 1 })).v };",
            ["broken.js"] = "const x = {};\nreturn x.a.b;",
        };
        var options = new CodeModeRunOptions
        {
            ResolveScript = (reference, _) => scripts.TryGetValue(reference.ScriptPath ?? string.Empty, out var code)
                ? ValueTask.FromResult(CodeModeSource.Prepare(code, reference.ScriptPath!))
                : throw new FileNotFoundException($"No script {reference.ScriptPath}."),
        };

        var result = await RunAsync("""
            const doubled = await workflow({ scriptPath: "double.js" }, { n: 21 });
            let failure = null, missing = null;
            try { await workflow({ scriptPath: "broken.js" }); } catch (e) { failure = e.message; }
            try { await workflow({ scriptPath: "nope.js" }); } catch (e) { missing = e.code; }
            return { doubled, failure, missing };
            """, options: options);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        using var value = JsonDocument.Parse(result.ValueJson!);
        Assert.AreEqual(42, value.RootElement.GetProperty("doubled").GetProperty("n").GetInt32());
        Assert.AreEqual(1, value.RootElement.GetProperty("doubled").GetProperty("echoed").GetInt32());
        StringAssert.StartsWith(value.RootElement.GetProperty("failure").GetString(), "broken.js (line 2):");
        Assert.AreEqual("codemode.workflowNotFound", value.RootElement.GetProperty("missing").GetString());
        Assert.IsTrue(result.Output.Any(static entry => entry.Text == "doubling"), "the output of the nested script joins the run's");
    }

    [TestMethod]
    public async Task Workflow_IsBoundedInDepth_AndAbsentWithoutAResolver()
    {
        var options = new CodeModeRunOptions
        {
            MaxWorkflowDepth = 2,
            ResolveScript = (_, _) => ValueTask.FromResult(CodeModeSource.Prepare("return await workflow({ scriptPath: 'self.js' });", "self.js")),
        };

        var deep = await RunAsync("return await workflow({ scriptPath: 'self.js' });", options: options);
        var absent = await RunAsync("try { await workflow({ scriptPath: 'x.js' }); } catch (e) { return e.message; }");

        Assert.AreEqual(CodeModeRunStatus.Failed, deep.Status);
        StringAssert.Contains(deep.Error!.Message, "2 levels deep at most");
        StringAssert.Contains(absent.ValueJson, "not available");
    }

    [TestMethod]
    public async Task RunsOfManySessions_RunAtTheSameTime()
    {
        var cache = new CodeModeScriptCache();
        var watch = Stopwatch.StartNew();
        var runs = Enumerable.Range(0, 40).Select(i =>
            new CodeModeHost(Api, cache).RunAsync(CodeModeSource.Prepare($"await tools.slow();\nreturn {i};"), Handler(slowMilliseconds: 400)));
        var results = await Task.WhenAll(runs);

        Assert.IsTrue(results.All(result => result.Status == CodeModeRunStatus.Completed));
        Assert.IsLessThan(3_000, watch.ElapsedMilliseconds, "40 runs that each await 400 ms ran in sequence");
    }
}
