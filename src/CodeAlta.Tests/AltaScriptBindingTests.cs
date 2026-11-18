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
        return (dispatcher, new AltaScriptBinding(dispatcher.Describe(AltaCallerIdentity.Cli, globalRoot), runsCommandLines: true));
    }

    private static Task<CodeModeRunResult> RunAsync(AltaCommandDispatcher dispatcher, AltaScriptBinding binding, string code)
        => new CodeModeHost(new CodeModeApi(binding.Functions), new CodeModeScriptCache())
            .RunAsync(CodeModeSource.Prepare(code), new AltaScriptCallHandler(dispatcher, binding, AltaCallerIdentity.Cli with { ScriptRunId = "run-test" }, null));

    [TestMethod]
    public void TheTree_BecomesFunctions_WithoutOlderNames()
    {
        using var root = TestTempDirectory.Create();
        var (_, binding) = Create(root.Path);
        var paths = binding.Functions.Select(static function => function.Path).ToHashSet(StringComparer.Ordinal);

        Assert.IsTrue(paths.Contains("alta.prompt.list"));
        Assert.IsTrue(paths.Contains("alta.prompt.create"));
        Assert.IsTrue(paths.Contains("alta.ask"), "a command that queues for the user is a command too");
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

        CollectionAssert.AreEqual(new[] { "prompt", "show", "--system", "--scope=global", "--", "reviewer" }, line!.Arguments.ToArray());
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
    public async Task APositionalValueThatStartsWithADash_IsAValue_NotAnOptionOfTheCommand()
    {
        using var root = TestTempDirectory.Create();
        var (dispatcher, binding) = Create(root.Path);

        // `prompt show --help` writes the help and succeeds: the value must reach the command as the id of a prompt.
        var result = await RunAsync(dispatcher, binding, """
            return await alta.prompt.show({ promptId: "--help", scope: "global" }).then(() => "ran as an option", (e) => e.name);
            """);

        Assert.AreEqual("\"AltaError\"", result.ValueJson, result.Error?.Message);
    }

    [TestMethod]
    public void WhatACommandTakes_IsWhatAScriptCanGive()
    {
        var binding = new AltaScriptBinding(
        [
            new AltaCommandDescriptor("pack add", "Adds.",
                [new AltaArgumentDescriptor("name", Optional: false, IsList: false), new AltaArgumentDescriptor(AltaArgumentDescriptor.RemainderName, Optional: true, IsList: true)],
                [
                    new AltaOptionDescriptor("optional", [], TakesValue: true) { ValueOptional = true },
                    new AltaOptionDescriptor("tag", ["t"], TakesValue: true),
                ]),
        ]);

        Assert.IsTrue(binding.TryCreateCommandLine("alta.pack.add", """{"name":"-n","rest":["--x","y"],"optional":true,"tag":["a","b"]}""", out var line, out var error), error?.Message);
        CollectionAssert.AreEqual(new[] { "pack", "add", "--optional", "--tag=a", "--tag=b", "--", "-n", "--x", "y" }, line!.Arguments.ToArray());

        Assert.IsTrue(binding.TryCreateCommandLine("alta.pack.add", """{"name":"n","optional":false}""", out line, out error), error?.Message);
        CollectionAssert.AreEqual(new[] { "pack", "add", "--optional=false", "--", "n" }, line!.Arguments.ToArray());

        Assert.IsFalse(binding.TryCreateCommandLine("alta.pack.add", """{"name":["a","b"]}""", out _, out error));
        StringAssert.Contains(error!.Message, "`name` takes one value, not an array");
        StringAssert.Contains(CodeModeDeclarations.RenderFunction(binding.Functions[0]), "optional?: boolean | string");
        Assert.IsFalse(binding.Contains(AltaScriptBinding.RunPath), "a binding gives command lines only when asked to");
    }

    [TestMethod]
    public async Task Run_RunsACommandLineAsItIs_AndGivesAllItWrote_AlsoWhenItFails()
    {
        using var root = TestTempDirectory.Create();
        var (dispatcher, binding) = Create(root.Path);

        var result = await RunAsync(dispatcher, binding, """
            const help = await alta.run({ args: ["prompt", "show", "--help"] });
            const asked = await alta.run({ args: ["prompt", "create", "reviewer", "--scope=global", "--stdin"], stdin: "Review." });
            const failed = await alta.run({ args: ["prompt", "show", "nope", "--scope=global"] });
            const refused = await alta.run({ args: "prompt list" }).catch((e) => e.name);
            return { help: help.output, helpRecords: help.records.length, created: asked.records[0].type, exitCode: failed.exitCode, error: failed.records[0].type, refused };
            """);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        using var value = JsonDocument.Parse(result.ValueJson!);
        StringAssert.Contains(value.RootElement.GetProperty("help").GetString(), "Usage: alta prompt show");
        Assert.AreEqual(0, value.RootElement.GetProperty("helpRecords").GetInt32());
        StringAssert.StartsWith(value.RootElement.GetProperty("created").GetString(), "alta.prompt");
        Assert.AreNotEqual(0, value.RootElement.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("alta.error", value.RootElement.GetProperty("error").GetString());
        Assert.AreEqual("TypeError", value.RootElement.GetProperty("refused").GetString());
        Assert.IsFalse(binding.IsReadOnly(AltaScriptBinding.RunPath), "a resumed script never reruns a command line by itself");
    }

    [TestMethod]
    public void WhatACommandWroteThatIsNoRecord_IsItsText_NeverAnEmptyResult()
    {
        var text = AltaScriptBinding.ReadResult(new AltaCommandResult { ExitCode = 0, Stdout = "Three items\n- a\n", Stderr = string.Empty, CorrelationId = "c", Duration = TimeSpan.Zero }, "plugin report");
        var none = AltaScriptBinding.ReadResult(new AltaCommandResult { ExitCode = 0, Stdout = """{"type":"alta.result","version":1}""" + "\n", Stderr = string.Empty, CorrelationId = "c", Duration = TimeSpan.Zero }, "notes clear");

        Assert.AreEqual("\"Three items\\n- a\"", text.ValueJson);
        Assert.IsNull(none.ValueJson);
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
