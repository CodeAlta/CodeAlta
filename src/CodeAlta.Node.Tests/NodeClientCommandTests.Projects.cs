using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Catalog;

namespace CodeAlta.Node.Tests;

public sealed partial class NodeClientCommandTests
{
    [TestMethod]
    public async Task Projects_WithoutANode_ListAddAndRemoveOnTheCatalogOfTheProfile()
    {
        using var root = new NodeTestRoot();
        var catalog = new CatalogOptions { GlobalRoot = root.DataRoot, StateRoot = root.DataRoot };
        var work = (await new SpaceCatalog(new ProjectCatalog(catalog)).CreateAsync("Work", cancellationToken: TestContext.CancellationToken)).Space!;

        var (code, output, error) = await RunProjectsAsync(root, ["projects", "add", root.ProjectRoot, "--space", "work", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        StringAssert.Contains(output, $"Added project (");
        StringAssert.Contains(output, $"{root.ProjectRoot}, in {work.Id}.");
        var project = (await new ProjectCatalog(catalog).LoadAsync(TestContext.CancellationToken)).Single();
        Assert.AreEqual(root.ProjectRoot, project.ProjectPath);
        CollectionAssert.AreEqual(new[] { work.Id }, project.Spaces.ToArray());

        (code, output, error) = await RunProjectsAsync(root, ["projects", "add", root.ProjectRoot, "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        StringAssert.Contains(output, "is in the catalog already");

        (code, output, error) = await RunProjectsAsync(root, ["projects", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        StringAssert.Contains(output, project.Id);
        StringAssert.Contains(output, root.ProjectRoot);
        StringAssert.Contains(output, "no node that listens runs on this profile");

        (code, _, error) = await RunProjectsAsync(root, ["projects", "add", Path.Combine(root.Root, "missing"), "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Failure, code);
        StringAssert.Contains(error, "is not a folder of this machine");
        (code, _, error) = await RunProjectsAsync(root, ["projects", "add", root.Home, "--space", "nope", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Failure, code);
        StringAssert.Contains(error, "No space is named 'nope'");
        (code, _, error) = await RunProjectsAsync(root, ["projects", "clone", root.ProjectRoot, "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Failure, code);
        StringAssert.Contains(error, "is not the URL of a repository this machine can clone");
        Assert.AreEqual(1, (await new ProjectCatalog(catalog).LoadAsync(TestContext.CancellationToken)).Count, "What is refused adds nothing.");

        (code, output, error) = await RunProjectsAsync(root, ["projects", "remove", project.Id, "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        StringAssert.Contains(output, $"Removed project ({project.Id}) from the catalog.");
        StringAssert.Contains(output, "its sessions were not looked at");
        Assert.AreEqual(0, (await new ProjectCatalog(catalog).LoadAsync(TestContext.CancellationToken)).Count);
        Assert.IsTrue(Directory.Exists(root.ProjectRoot), "The folder of a removed project stays.");

        (code, _, error) = await RunProjectsAsync(root, ["projects", "remove", "nobody", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Failure, code);
        StringAssert.Contains(error, "No project is named 'nobody'");
        (code, _, error) = await RunProjectsAsync(root, ["projects", "frob", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.InvalidUsage, code);
        StringAssert.Contains(error, "'frob' is not a command of projects");
    }

    [TestMethod]
    public async Task Projects_GoThroughTheRunningNode_WhichKeepsAProjectThatHasSessions()
    {
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        // A checkout of https://example.test/acme/widget already where a clone of it goes: it is added as it is, offline.
        var checkout = Path.Combine(root.CloneRoot, "example.test", "acme", "widget");
        try
        {
            Git(root.Root, "init", "-q", "-b", "main", checkout);
        }
        catch (Win32Exception)
        {
            Assert.Inconclusive("git is not installed.");
        }

        Git(checkout, "remote", "add", "origin", "https://example.test/acme/widget");
        var viewerRoot = Path.Combine(root.Root, "viewer-alta");
        var provider = new ScriptedNodeProvider();
        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runError = new CapturingWriter();
        var node = new NodeApplication(new CapturingWriter(), runError)
        {
            InitializeLogging = false,
            ConfigureHost = options => root.Isolate(options) with { ConfigureModelProviders = provider.Register },
            Started = (_, stop) => started.TrySetResult(stop),
        };
        var running = node.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0", "--name", "test-node"]);
        var stop = await started.Task.WithinAsync(Patience, token);
        try
        {
            var (code, output, error) = await RunProjectsAsync(root, ["projects", "add", root.ProjectRoot, "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "Added project (");
            (code, output, error) = await RunProjectsAsync(root, ["projects", "clone", "https://example.test/acme/widget", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "held a checkout of example.test/acme/widget already");
            StringAssert.Contains(output, checkout);

            (code, output, error) = await RunProjectsAsync(root, ["projects", "list", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, root.ProjectRoot);
            StringAssert.Contains(output, checkout);
            Assert.IsFalse(output.Contains("no node that listens", StringComparison.Ordinal), "The running node lists its projects.");
            var catalog = new CatalogOptions { GlobalRoot = root.DataRoot, StateRoot = root.DataRoot };
            var project = (await new ProjectCatalog(catalog).GetByPathAsync(root.ProjectRoot, token))!;

            // A session of the project keeps it in the catalog.
            var pairOutput = new CapturingWriter();
            var pairError = new CapturingWriter();
            var pairing = Client(pairOutput, pairError).RunAsync(["pair", "--data-root", root.DataRoot, "--minutes", "1"]);
            var link = await WaitForAsync(() => LinkPattern().Match(pairOutput.Text) is { Success: true } match ? match.Value : null, pairing, pairError, token);
            (code, _, error) = await RunAsync(["connect", link, "--data-root", viewerRoot, "--name", "viewer"]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            Assert.AreEqual(NodeExitCodes.Success, await pairing.WithinAsync(Patience, token), pairError.Text);
            (code, output, error) = await RunAsync(["remote", "test-node", "create", "--project", project.Id, "Hello", "--data-root", viewerRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);

            (code, _, error) = await RunProjectsAsync(root, ["projects", "remove", project.Id, "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Failure, code);
            StringAssert.Contains(error, "has 1 session(s) on this machine and was not removed");
            (code, output, error) = await RunProjectsAsync(root, ["projects", "remove", "widget", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "from the catalog.");
            Assert.IsFalse(output.Contains("were not looked at", StringComparison.Ordinal), "The running node looked at its sessions.");
            Assert.IsTrue(Directory.Exists(checkout));
            Assert.IsNull(await new ProjectCatalog(catalog).GetByPathAsync(checkout, token));
        }
        finally
        {
            stop();
        }

        Assert.AreEqual(NodeExitCodes.Success, await running.WithinAsync(Patience, token), runError.Text);
    }

    // A client with the clone root of the node of the test root, as the node of the profile would run.
    private async Task<(int Code, string Output, string Error)> RunProjectsAsync(NodeTestRoot root, string[] args)
    {
        var output = new CapturingWriter();
        var error = new CapturingWriter();
        var client = new NodeApplication(output, error)
        {
            InitializeLogging = false, ClientStop = TestContext.CancellationToken, ServiceEnvironment = static () => null, ConfigureHost = root.Isolate,
        };
        var code = await client.RunAsync(args).WithinAsync(Patience, TestContext.CancellationToken);
        return (code, output.Text, error.Text);
    }

    private static void Git(string folder, params string[] arguments)
    {
        Directory.CreateDirectory(folder);
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
    }
}
