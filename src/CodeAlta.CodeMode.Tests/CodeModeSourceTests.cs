using System.Text.Json;
using CodeAlta.CodeMode;

namespace CodeAlta.CodeMode.Tests;

[TestClass]
public sealed class CodeModeSourceTests
{
    private static async Task<CodeModeRunResult> RunAsync(string code, string? argumentsJson = null)
    {
        var host = new CodeModeHost(CodeModeApi.Empty, new CodeModeScriptCache());
        return await host.RunAsync(CodeModeSource.Prepare(code), new DelegateHandler(), new CodeModeRunOptions { ArgumentsJson = argumentsJson });
    }

    [TestMethod]
    public void Pragma_SetsTheOptions()
    {
        var source = CodeModeSource.Prepare("""
            // @exec: {"title": "Find bugs", "yield_ms": 5000, "max_output_tokens": 1000, "timeout_ms": 60000}
            return 1;
            """);

        Assert.IsNull(source.Error);
        Assert.AreEqual("Find bugs", source.Options.Title);
        Assert.AreEqual(5000, source.Options.YieldMilliseconds);
        Assert.AreEqual(1000, source.Options.MaxOutputTokens);
        Assert.AreEqual(60000, source.Options.TimeoutMilliseconds);
    }

    [TestMethod]
    public void Pragma_TakesCodexNames_AndRefusesUnknownOptions()
    {
        Assert.AreEqual(2000, CodeModeSource.Prepare("// @exec: {\"yield_time_ms\": 2000}\nreturn 1;").Options.YieldMilliseconds);

        var source = CodeModeSource.Prepare("// @exec: {\"yeild_ms\": 2000}\nreturn 1;");

        Assert.AreEqual(CodeModeErrorKind.Syntax, source.Error?.Kind);
        StringAssert.Contains(source.Error!.Message, "Unknown option `yeild_ms`");
    }

    [TestMethod]
    public void Pragma_ResumeOptions_GoWithTheRunTheyName()
    {
        var resume = CodeModeSource.Prepare("// @exec: {\"resume\": \"run-1a2b3c\", \"from\": \"merge#1\", \"rerun_in_flight\": true}");

        Assert.IsNull(resume.Error, resume.Error?.Message);
        Assert.AreEqual("run-1a2b3c", resume.Options.Resume);
        Assert.AreEqual("merge#1", resume.Options.From);
        Assert.IsTrue(resume.Options.RerunInFlight);
        Assert.AreEqual(string.Empty, CodeModeSource.BodyOf("// @exec: {\"resume\": \"run-1a2b3c\"}\n"), "only the options line: no body");
        Assert.AreEqual(CodeModeSource.BodyHashOf("// @exec: {\"title\": \"A\"}\r\nreturn 1;\r\n"), CodeModeSource.BodyHashOf("// @exec: {\"resume\": \"run-1\"}\nreturn 1;"),
            "the body leaves out the options and the line ends");
        StringAssert.Contains(CodeModeSource.Prepare("// @exec: {\"replay\": \"all\"}").Error?.Message, "go with `resume`");
        StringAssert.Contains(CodeModeSource.Prepare("// @exec: {\"resume\": \"run-1\", \"replay\": \"some\"}").Error?.Message, "\"resume\" or \"all\"");
        StringAssert.Contains(CodeModeSource.Prepare("// @exec: {\"resume\": \"run-1\", \"replay\": \"all\", \"from\": \"b\"}").Error?.Message, "does not go with");
    }

    [TestMethod]
    public void TypeScript_IsRefusedWithTheFix()
    {
        var source = CodeModeSource.Prepare("const a = 1;\nconst names: string[] = [];\nreturn names;");

        Assert.AreEqual(CodeModeErrorKind.Syntax, source.Error?.Kind);
        Assert.AreEqual(2, source.Error!.Location?.Line);
        StringAssert.StartsWith(source.Error.Message, "TypeScript syntax is not supported");
    }

    [TestMethod]
    public void Import_IsRefused()
    {
        var source = CodeModeSource.Prepare("import { review } from \"lib:review\";\nreturn review();");

        StringAssert.Contains(source.Error?.Message, "workflow(");
    }

    [TestMethod]
    public void AScriptThatDeclaresAFunctionOfScripts_IsRefused_WithTheFix()
    {
        // A model wrote this: its step ran the work, and the run recorded no step of its meta.
        var shadowed = CodeModeSource.Prepare("export const meta = { steps: [{ id: \"research\", title: \"Research\" }] };\nconst step = (id, fn) => fn();\nreturn step(\"research\", () => 1);");
        var declared = CodeModeSource.Prepare("async function agent(prompt) { return prompt; }\nreturn agent(\"x\");");

        StringAssert.Contains(shadowed.Error?.Message, "Remove the declaration and call `step(...)`");
        Assert.AreEqual(2, shadowed.Error?.Location?.Line);
        StringAssert.Contains(declared.Error?.Message, "`agent` is a function of scripts");
        // A local of the same name inside a function is the script's own business, and other names are free.
        Assert.IsNull(CodeModeSource.Prepare("const run = (step) => step + 1;\nconst text = \"a\";\nreturn run(1);").Error);
    }

    [TestMethod]
    public async Task MarkdownFences_AreIgnored_AndLinesKeepTheirNumbers()
    {
        var result = await RunAsync("```js\nconst a = {};\nreturn a.b.c;\n```");

        Assert.AreEqual(3, result.Error?.Location?.Line);
    }

    [TestMethod]
    public async Task ExportDefaultFunction_RunsWithTheArguments()
    {
        var result = await RunAsync("""
            export const meta = { name: "double", params: { n: "number" } };
            export default async function run({ n }) { return n * 2; }
            """, """{"n":21}""");

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("42", result.ValueJson);
    }

    [TestMethod]
    public async Task ALoneArrowFunction_IsCalled()
    {
        var result = await RunAsync("async () => { const x = await Promise.resolve(40); return x + 2; }");

        Assert.AreEqual("42", result.ValueJson);
    }

    [TestMethod]
    public async Task ALoneFunctionDeclaration_IsCalled()
    {
        var result = await RunAsync("async function main() {\n  return 'ran';\n}");

        Assert.AreEqual("\"ran\"", result.ValueJson);
    }

    [TestMethod]
    public void Meta_IsReadWithoutRunning()
    {
        var source = CodeModeSource.Prepare("""
            export const meta = {
              name: "review-pr",
              description: "Reviews a pull request from three angles.",
              params: { pr: { type: "integer", description: "Number" }, depth: { type: "string", default: "quick" } },
              steps: [
                { id: "fetch", title: "Fetch the pull request" },
                { id: "review", title: "Review", detail: "one session per lens", each: true },
                { id: "merge", title: "Merge the findings", after: ["review"] },
              ],
            };
            return 1;
            """);

        Assert.IsNull(source.Error, source.Error?.Message);
        var meta = source.Descriptor!;
        Assert.AreEqual("review-pr", meta.Name);
        CollectionAssert.AreEqual(new[] { "fetch", "review", "merge" }, meta.Steps.Select(step => step.Id).ToArray());
        Assert.IsTrue(meta.Steps[1].Each);
        CollectionAssert.AreEqual(new[] { "review" }, meta.Steps[2].After!.ToArray());
        using var schema = JsonDocument.Parse(meta.ParametersSchemaJson!);
        CollectionAssert.AreEqual(new[] { "pr" }, schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [TestMethod]
    public void ClaudeCodePhases_AreAcceptedAsSteps()
    {
        var source = CodeModeSource.Prepare("""
            export const meta = { name: "scan", description: "x", phases: [{ title: "Scan logs", detail: "grep" }, { title: "Fix" }] };
            phase;
            """);

        CollectionAssert.AreEqual(new[] { "scan-logs", "fix" }, source.Descriptor!.Steps.Select(step => step.Id).ToArray());
    }

    [TestMethod]
    public void Meta_ThatComputesAValue_IsRefused()
    {
        var source = CodeModeSource.Prepare("const n = 3;\nexport const meta = { name: `x${n}` };\nreturn 1;");

        Assert.AreEqual(CodeModeErrorKind.Syntax, source.Error?.Kind);
        StringAssert.Contains(source.Error!.Message, "plain literal");
    }

    [TestMethod]
    public void Meta_WithAnUnknownAfter_IsRefused()
    {
        var source = CodeModeSource.Prepare("export const meta = { steps: [{ id: \"a\", after: [\"nope\"] }] };");

        StringAssert.Contains(source.Error?.Message, "`nope`");
    }
}
