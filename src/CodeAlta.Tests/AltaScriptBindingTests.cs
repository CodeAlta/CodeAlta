using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.LiveTool;
using CodeAlta.LiveTool.Scripts;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AltaScriptBindingTests
{
    private static (AltaCommandDispatcher Dispatcher, AltaScriptBinding Binding) Create(string globalRoot)
    {
        var registry = new AltaCommandRegistry();
        var services = new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = globalRoot }).Add(registry);
        var dispatcher = new AltaCommandDispatcher(registry, services);
        return (dispatcher, new AltaScriptBinding(dispatcher.Describe(AltaCallerIdentity.Cli, globalRoot)));
    }

    private static Task<CodeModeRunResult> RunAsync(AltaCommandDispatcher dispatcher, AltaScriptBinding binding, string code)
        => new CodeModeHost(new CodeModeApi(binding.Functions), new CodeModeScriptCache())
            .RunAsync(CodeModeSource.Prepare(code), new AltaScriptCallHandler(dispatcher, binding, AltaCallerIdentity.Cli with { ScriptRunId = "run-test" }, null));

    [TestMethod]
    public void TheTree_BecomesFunctions_WithoutOlderNamesOrCommandsThatWaitForTheUser()
    {
        using var root = TestTempDirectory.Create();
        var (_, binding) = Create(root.Path);
        var paths = binding.Functions.Select(static function => function.Path).ToHashSet(StringComparer.Ordinal);

        Assert.IsTrue(paths.Contains("alta.prompt.list"));
        Assert.IsTrue(paths.Contains("alta.prompt.create"));
        Assert.IsFalse(paths.Any(static path => path.StartsWith("alta.ask", StringComparison.Ordinal)), "a script cannot wait for the user");
        Assert.IsFalse(paths.Contains("alta.note.get"), "older names are left out");
        var declarations = new CodeModeApi(binding.Functions).Declarations;
        StringAssert.Contains(declarations, "declare const alta: {");
        StringAssert.Contains(declarations, "prompt: {");
    }

    [TestMethod]
    public async Task AScript_CallsCommandsWithAnObject_AndReceivesTheirRecords()
    {
        using var root = TestTempDirectory.Create();
        var (dispatcher, binding) = Create(root.Path);

        var result = await RunAsync(dispatcher, binding, """
            await alta.prompt.create({ promptId: "reviewer", scope: "global", stdin: "Review the changes." });
            const shown = await alta.prompt.show({ promptId: "reviewer", scope: "global" });
            return { type: shown.type, content: shown.content, version: shown.version, correlationId: shown.correlationId };
            """);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        using var value = JsonDocument.Parse(result.ValueJson!);
        Assert.AreEqual("alta.prompt", value.RootElement.GetProperty("type").GetString());
        StringAssert.Contains(value.RootElement.GetProperty("content").GetString(), "Review the changes.");
        Assert.IsFalse(value.RootElement.TryGetProperty("version", out _), "the envelope fields of a record are left out");
        Assert.IsFalse(value.RootElement.TryGetProperty("correlationId", out _));
    }

    [TestMethod]
    public async Task AFailedCommand_ThrowsAnAltaErrorWithItsCode()
    {
        using var root = TestTempDirectory.Create();
        var (dispatcher, binding) = Create(root.Path);

        var result = await RunAsync(dispatcher, binding, """
            try { await alta.prompt.show({ promptId: "nope", scope: "global" }); return "no error"; }
            catch (e) { return [e.name, typeof e.code, e.data.exitCode > 0]; }
            """);

        Assert.AreEqual("""["AltaError","string",true]""", result.ValueJson, result.Error?.Message);
    }

    [TestMethod]
    public void AnUnknownProperty_IsRefusedWithTheNearestName()
    {
        using var root = TestTempDirectory.Create();
        var (_, binding) = Create(root.Path);

        Assert.IsFalse(binding.TryCreateCommandLine("alta.prompt.list", """{"scop":"global"}""", out _, out var error));

        Assert.AreEqual("TypeError", error!.Name);
        StringAssert.Contains(error.Message, "Did you mean `scope`?");
    }

    [TestMethod]
    public void Values_BecomeOptions_FlagsAndPositionalArguments()
    {
        using var root = TestTempDirectory.Create();
        var (_, binding) = Create(root.Path);

        Assert.IsTrue(binding.TryCreateCommandLine("alta.prompt.show", """{"promptId":"reviewer","system":true,"scope":"global"}""", out var line, out var error), error?.Message);

        CollectionAssert.AreEqual(new[] { "prompt", "show", "reviewer", "--system", "--scope=global" }, line!.Arguments.ToArray());
        Assert.IsNull(line.Stdin);
    }

    [TestMethod]
    public void TheTree_AndItsAnnotations_AgreeWithEachOther()
    {
        using var root = TestTempDirectory.Create();
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = root.Path }).Add(registry));
        var commands = dispatcher.Describe(AltaCallerIdentity.Cli, root.Path).ToDictionary(static command => command.Path, StringComparer.Ordinal);

        // A script is told what a command changes: every command declares its policy.
        var withoutPolicy = commands.Values.Where(static command => command.Policy is null).Select(static command => command.Path).ToArray();
        Assert.IsEmpty(withoutPolicy, "commands without a policy: " + string.Join(", ", withoutPolicy));

        // Each annotation names a command and its options as the tree has them, so the declarations never drift.
        foreach (var (path, annotation) in AltaScriptBinding.Annotations)
        {
            if (!commands.TryGetValue(path, out var command))
            {
                // A group exists only where its service or plugin is (`search`, `issue`, `job`, `mcp`): its whole
                // group is absent then, never one command of a group that is there.
                var group = path.Split(' ')[0];
                Assert.IsFalse(commands.Keys.Any(existing => existing.Split(' ')[0] == group), $"`{path}` is annotated but is not a command of `{group}`");
                continue;
            }

            var names = command.Options.Select(static option => option.Name).Concat(command.Arguments.Select(static argument => argument.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var name in annotation.Values.Keys.Concat(annotation.Required).Concat(annotation.Defaults.Keys))
            {
                Assert.IsTrue(names.Contains(name), $"`{path}` has no option or argument `{name}`");
            }

            if (annotation.Stdin != AltaStdinKind.None)
            {
                Assert.IsTrue(names.Contains("stdin"), $"`{path}` reads no stdin");
            }

            if (annotation.Result?.TrimEnd('[', ']') is { } result && !result.Contains('|', StringComparison.Ordinal) && result != "AltaRecord")
            {
                StringAssert.Contains(AltaScriptBinding.Types, $"interface {result} ", $"`{path}` answers `{result}`, which is not declared");
            }
        }
    }

    [TestMethod]
    public void AMissingRequiredArgument_IsRefusedBeforeTheCommandRuns()
    {
        using var root = TestTempDirectory.Create();
        var (_, binding) = Create(root.Path);

        Assert.IsFalse(binding.TryCreateCommandLine("alta.prompt.show", "{}", out _, out var error));

        StringAssert.Contains(error!.Message, "missing `promptId`");
    }
}
