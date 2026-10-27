using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeModeScriptLibraryTests
{
    private const string Review = """
        export const meta = { name: "review", description: "Reviews a pull request.", params: { pr: { type: "integer" }, depth: { type: "string", default: "quick" } } };
        return args.pr;
        """;

    [TestMethod]
    public void Save_WritesTheScopeOfTheScript_AndAProjectScriptWinsOverYours()
    {
        using var root = TestTempDirectory.Create();
        var (library, project) = Create(root.Path);

        var global = library.Save("review", CodeModeScriptScope.Global, Review, null, null);
        var local = library.Save("review", CodeModeScriptScope.Project, Review + "\n// project", project, null);
        library.Save("triage", CodeModeScriptScope.Global, "return 1;", null, null);

        Assert.AreEqual(Path.Combine(library.GlobalFolder, "review.js"), global.Script!.Path);
        Assert.AreEqual(Path.Combine(project, ".alta", "scripts", "review.js"), local.Script!.Path);
        Assert.AreEqual(CodeModeScriptScope.Project, library.Find("review", project)!.Scope);
        Assert.AreEqual(CodeModeScriptScope.Global, library.Find("review", null)!.Scope);
        CollectionAssert.AreEqual(new[] { ("review", CodeModeScriptScope.Project), ("triage", CodeModeScriptScope.Global) },
            library.List(project).Select(static script => (script.Name, script.Scope)).ToArray());
        Assert.AreEqual("Reviews a pull request.", library.Find("review", project)!.Source.Descriptor!.Description);
    }

    [TestMethod]
    public void Save_RefusesABadName_AScriptThatDoesNotParse_AndACopyThatChangedSinceItsHash()
    {
        using var root = TestTempDirectory.Create();
        var (library, _) = Create(root.Path);
        var first = library.Save("review", CodeModeScriptScope.Global, Review, null, null).Script!;
        library.Save("review", CodeModeScriptScope.Global, Review + "\n// changed by another session", null, first.Hash);

        StringAssert.Contains(library.Save("Review", CodeModeScriptScope.Global, Review, null, null).Refusal, "not a script name");
        StringAssert.Contains(library.Save("broken", CodeModeScriptScope.Global, "return (;", null, null).Refusal, "does not parse");
        StringAssert.Contains(library.Save("review", CodeModeScriptScope.Global, Review, null, first.Hash).Refusal, "changed since it was edited");
        StringAssert.Contains(library.Save("review", CodeModeScriptScope.BuiltIn, Review, null, null).Refusal, "samples");
        StringAssert.Contains(library.Find("review", null)!.Source.Original, "another session");
    }

    [TestMethod]
    public async Task AltaScript_EditsAScriptThroughACopy_ChecksIt_SavesIt_AndDeletesIt()
    {
        using var root = TestTempDirectory.Create();
        var (library, project) = Create(root.Path);
        library.Save("review", CodeModeScriptScope.Project, Review, project, null);
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection()
            .Add(new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "home") }).Add(registry).Add(library));
        Task<AltaCommandResult> Invoke(string? stdin, params string[] args) => dispatcher.InvokeAsync(args, stdin, cwd: project).AsTask();

        var listed = await Invoke(null, "script", "list");
        var declared = await Invoke(null, "script", "show", "review", "--declarations");
        var edited = await Invoke(null, "script", "edit", "review");
        using var edit = JsonDocument.Parse(edited.Stdout.Split('\n').Single(static line => line.Contains("\"alta.script.edit\"", StringComparison.Ordinal)));
        var copy = edit.RootElement.GetProperty("path").GetString()!;
        var hash = edit.RootElement.GetProperty("hash").GetString()!;
        File.WriteAllText(copy, Review.Replace("return args.pr;", "return args.pr * 2;", StringComparison.Ordinal));
        var checkedCopy = await Invoke(null, "script", "check", copy);
        var badCheck = await Invoke("return (;", "script", "check", "--stdin");
        var saved = await Invoke(null, "script", "save", "review", "--from", copy, "--hash", hash);
        var stale = await Invoke(null, "script", "save", "review", "--from", copy, "--hash", hash);
        var deleted = await Invoke(null, "script", "delete", "review");
        var gone = await Invoke(null, "script", "show", "review");

        StringAssert.Contains(listed.Stdout, "\"name\":\"review\"");
        StringAssert.Contains(listed.Stdout, "\"scope\":\"project\"");
        StringAssert.Contains(declared.Stdout, "declare function workflow(name: \\\"review\\\", args: { pr: number; depth?: string }): Promise<unknown>;");
        StringAssert.StartsWith(copy, Path.Combine(root.Path, "home", "scratchpad", "scripts"));
        Assert.AreEqual(AltaExitCodes.Success, checkedCopy.ExitCode, checkedCopy.Transcript);
        StringAssert.Contains(checkedCopy.Stdout, "\"valid\":true");
        Assert.AreNotEqual(AltaExitCodes.Success, badCheck.ExitCode);
        StringAssert.Contains(badCheck.Stdout, "\"valid\":false");
        Assert.AreEqual(AltaExitCodes.Success, saved.ExitCode, saved.Transcript);
        StringAssert.Contains(stale.Transcript, "script.saveRefused");
        Assert.AreEqual(AltaExitCodes.Success, deleted.ExitCode, deleted.Transcript);
        StringAssert.Contains(gone.Transcript, "script.notFound");
    }

    [TestMethod]
    public void ProjectRootOf_FindsTheNearestFolderWithAltaOrGit()
    {
        using var root = TestTempDirectory.Create();
        var (_, project) = Create(root.Path);
        var deep = Directory.CreateDirectory(Path.Combine(project, "src", "app")).FullName;

        Assert.AreEqual(project, CodeModeScriptLibrary.ProjectRootOf(deep));
        Assert.IsNull(CodeModeScriptLibrary.ProjectRootOf(null));
    }

    private static (CodeModeScriptLibrary Library, string Project) Create(string root)
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        return (new CodeModeScriptLibrary(new CatalogOptions { GlobalRoot = Path.Combine(root, "home") }), project);
    }
}
