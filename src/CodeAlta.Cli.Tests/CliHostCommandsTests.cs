using System.Diagnostics;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Cli.Tests;

/// <summary><c>altacli alta</c>, <c>models</c> and <c>auth check</c>, and the executable itself.</summary>
[TestClass]
public sealed class CliHostCommandsTests
{
    private const string Plain = "--no-plugins";

    [TestMethod]
    public async Task Alta_SessionList_Runs_AndAnExcludedRootIsUnknown()
    {
        using var root = new CliTestRoot();
        // A session is listed under its project: the folder is a project of the catalog (a session of a folder outside it is
        // found by its id).
        await new CodeAlta.Catalog.ProjectCatalog(new CodeAlta.Catalog.CatalogOptions { GlobalRoot = root.Profile }).UpsertFromPathAsync(root.ProjectRoot);
        var provider = new FakeProvider();
        var created = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "hello"]);
        var sessionId = CliStorageTests.SessionIdOf(created);

        var list = await root.RunAsync(provider, ["alta", Plain, "session", "list", "--state", "all"]);
        Assert.AreEqual(0, list.ExitCode, list.Stdout + list.Stderr);
        StringAssert.StartsWith(list.Stdout, "{\"type\":\"alta.result\"");
        StringAssert.Contains(list.Stdout, sessionId, "The session the run wrote is read from its journal: " + list.Stdout);

        var ask = await root.RunAsync(provider, ["alta", Plain, "ask", "--stdin"], stdin: "{}");
        Assert.AreNotEqual(0, ask.ExitCode, ask.Stdout);

        var help = await root.RunAsync(provider, ["alta", Plain, "--help"]);
        Assert.AreEqual(0, help.ExitCode);
        StringAssert.Contains(help.Stdout, "session");
        Assert.IsFalse(help.Stdout.Contains("\n  ask ", StringComparison.Ordinal), "ask is not offered.");
        Assert.IsFalse(help.Stdout.Contains("\n  reminder ", StringComparison.Ordinal), "reminder is not offered.");

        var tools = await root.RunAsync(provider, ["alta", Plain, "tool", "list"]);
        Assert.AreEqual(0, tools.ExitCode, tools.Stdout + tools.Stderr);
        Assert.IsFalse(tools.Stdout.Contains("\"review request\"", StringComparison.Ordinal), "The registry of the CLI is the one listed.");
    }

    [TestMethod]
    public async Task Alta_FindsAndListsTheSessionsOfARun_InAFolderThatIsNoProject()
    {
        // altacli registers no project: the folder of a run stays outside the catalog, as the folder of a script's fixture.
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var sessionId = CliStorageTests.SessionIdOf(await root.RunAsync(provider, ["run", Plain, "--isolated", "--format", "jsonl", "hello"]));
        var elsewhere = Path.Combine(root.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        // By its id, from another folder, as `run --resume` finds it.
        var model = await root.RunAsync(provider, ["alta", Plain, "--isolated", "-C", elsewhere, "session", "model", sessionId]);
        Assert.AreEqual(0, model.ExitCode, model.Stdout + model.Stderr);
        StringAssert.Contains(model.Stdout, "\"alta.model.selection\"");
        StringAssert.Contains(model.Stdout, sessionId);

        // Listed for the folder of the command.
        var list = await root.RunAsync(provider, ["alta", Plain, "--isolated", "-C", root.ProjectRoot, "session", "list", "--state", "all"]);
        Assert.AreEqual(0, list.ExitCode, list.Stdout + list.Stderr);
        StringAssert.Contains(list.Stdout, sessionId);
        Assert.AreEqual(0, CliTestRoot.Files(Path.Combine(root.Profile, "projects")).Length, "No project file is written.");
    }

    [TestMethod]
    public async Task Alta_LeavesTheSpacesProjectsAndGlobalPromptsOfTheProfileAsTheyAre()
    {
        using var root = new CliTestRoot();
        var catalog = new CodeAlta.Catalog.ProjectCatalog(new CodeAlta.Catalog.CatalogOptions { GlobalRoot = root.Profile });
        var project = await catalog.UpsertFromPathAsync(root.ProjectRoot);
        var projectFile = project.SourcePath!;
        var provider = new FakeProvider();
        var sessionId = CliStorageTests.SessionIdOf(await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "hello"]));
        var other = Path.Combine(root.Root, "other");
        Directory.CreateDirectory(other);

        var space = await root.RunAsync(provider, ["alta", Plain, "space", "create", "--name", "Work"]);
        Assert.AreNotEqual(0, space.ExitCode, space.Stdout);
        StringAssert.Contains(space.Stdout, "read-only");
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Profile, "spaces")), "No space file is written.");

        var globalPrompt = await root.RunAsync(provider, ["alta", Plain, "prompt", "create", "reviewer", "--scope", "global", "--content", "Review."]);
        Assert.AreNotEqual(0, globalPrompt.ExitCode, globalPrompt.Stdout);
        StringAssert.Contains(globalPrompt.Stdout, "prompt.readOnly");
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Profile, "prompts")), "No global prompt is written.");

        // The prompts of the project are the project's files: they stay writable.
        var projectPrompt = await root.RunAsync(provider, ["alta", Plain, "prompt", "create", "reviewer", "--scope", "project", "--content", "Review."]);
        Assert.AreEqual(0, projectPrompt.ExitCode, projectPrompt.Stdout);
        Assert.IsTrue(File.Exists(Path.Combine(root.ProjectRoot, ".alta", "prompts", "agents", "reviewer.prompt.md")));

        var added = await root.RunAsync(provider, ["alta", Plain, "project", "add", other]);
        Assert.AreNotEqual(0, added.ExitCode, added.Stdout);
        StringAssert.Contains(added.Stdout, "read-only");

        // Refused before anything is deleted: the project file and the session stay.
        var removed = await root.RunAsync(provider, ["alta", Plain, "project", "remove", project.Slug, "--delete-sessions"]);
        Assert.AreNotEqual(0, removed.ExitCode, removed.Stdout);
        StringAssert.Contains(removed.Stdout, "project.readOnly");
        Assert.IsTrue(File.Exists(projectFile));
        StringAssert.Contains((await root.RunAsync(provider, ["alta", Plain, "session", "list", "--state", "all"])).Stdout, sessionId);
        CollectionAssert.AreEqual(new[] { Path.GetFileName(projectFile) }, CliTestRoot.Files(Path.Combine(root.Profile, "projects")));
    }

    [TestMethod]
    public async Task Models_ListsTheModelsOfTheReadyProviders()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var text = await root.RunAsync(provider, ["models", Plain]);
        Assert.AreEqual(0, text.ExitCode, text.Stderr);
        Assert.AreEqual("fake:fake-model  Fake Model\n", text.Stdout);

        var jsonl = await root.RunAsync(provider, ["models", Plain, "--format", "jsonl", "--provider", "fake"]);
        Assert.AreEqual(0, jsonl.ExitCode, jsonl.Stderr);
        var model = JsonDocument.Parse(jsonl.Lines.Single()).RootElement;
        Assert.AreEqual("fake:fake-model", model.GetProperty("ref").GetString());
        Assert.IsTrue(model.GetProperty("default").GetBoolean());

        Assert.AreEqual(CliExitCodes.InvalidUsage, (await root.RunAsync(provider, ["models", Plain, "--provider", "nope"])).ExitCode);
        var notReady = await root.RunAsync(new FakeProvider(availability: ModelProviderAvailability.Failed), ["models", Plain, "--provider", "fake"]);
        Assert.AreEqual(CliExitCodes.ProviderNotReady, notReady.ExitCode, notReady.Stderr);
    }

    [TestMethod]
    public async Task AuthCheck_ExitsWithZeroSixOrTwo()
    {
        using var root = new CliTestRoot();

        var ready = await root.RunAsync(new FakeProvider(), ["auth", "check", Plain, "--provider", "fake", "--format", "jsonl"]);
        Assert.AreEqual(0, ready.ExitCode, ready.Stderr);
        var report = JsonDocument.Parse(ready.Lines.Single()).RootElement;
        Assert.IsTrue(report.GetProperty("ready").GetBoolean());
        Assert.AreEqual(1, report.GetProperty("models").GetInt32());

        var notReady = await root.RunAsync(new FakeProvider(availability: ModelProviderAvailability.Failed), ["auth", "check", Plain, "--provider", "fake"]);
        Assert.AreEqual(CliExitCodes.ProviderNotReady, notReady.ExitCode, notReady.Stdout);
        StringAssert.Contains(notReady.Stdout, "not ready");

        Assert.AreEqual(CliExitCodes.InvalidUsage, (await root.RunAsync(new FakeProvider(), ["auth", "check", Plain, "--provider", "unknown"])).ExitCode);
        Assert.AreEqual(CliExitCodes.InvalidUsage, (await root.RunAsync(new FakeProvider(), ["auth", "check", Plain])).ExitCode);
    }

    [TestMethod]
    public async Task Executable_PrintsItsHelpAndVersion()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "altacli.exe" : "altacli");
        Assert.IsTrue(File.Exists(executable), executable);

        var help = await RunProcessAsync(executable, "--help");
        Assert.AreEqual(0, help.ExitCode, help.Stderr);
        StringAssert.Contains(help.Stdout, "Usage: altacli <command> [options]");
        StringAssert.Contains(help.Stdout, "run");

        var version = await RunProcessAsync(executable, "--version");
        Assert.AreEqual(0, version.ExitCode, version.Stderr);
        Assert.IsFalse(string.IsNullOrWhiteSpace(version.Stdout));

        var invalid = await RunProcessAsync(executable, "--no-such-option");
        Assert.AreEqual(CliExitCodes.InvalidUsage, invalid.ExitCode);
        Assert.AreEqual(string.Empty, invalid.Stdout, "Errors go to standard error.");

        // A profile that does not exist stops the run before anything is read or written.
        var missingProfile = Path.Combine(Path.GetTempPath(), "CodeAlta-cli-missing-" + Guid.NewGuid().ToString("N"));
        var noProfile = await RunProcessAsync(executable, "run", "--profile", missingProfile, "--format", "jsonl", "hello");
        Assert.AreEqual(CliExitCodes.InvalidUsage, noProfile.ExitCode, noProfile.Stderr);
        StringAssert.Contains(noProfile.Stdout, "\"method\":\"_codealta/result\"");
        Assert.IsFalse(Directory.Exists(missingProfile));
    }

    private static async Task<CliRun> RunProcessAsync(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new CliRun(process.ExitCode, await stdout, await stderr);
    }
}
