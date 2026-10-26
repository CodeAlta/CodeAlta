using System.Diagnostics;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Sandbox;
using CodeAlta.Sandbox.Mxc;
using CodeAlta.Sandbox.Worker;

namespace CodeAlta.Cli.Tests;

// The worker of a sandboxed session as it runs: altacli started with --sandbox-worker in a real container. Skipped
// where this computer has no sandbox, as the browser tests are without Edge.
[TestClass]
public sealed class CliSandboxWorkerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    [TestMethod]
    public async Task TheWorkerInAContainer_WritesItsProject_AndNeitherItsToolsNorItsCommandsReadTheHomeFolder()
    {
        var backend = new MxcSandboxBackend();
        if (backend.GetAvailability() is { IsAvailable: false } availability) Assert.Inconclusive(availability.Reason ?? "No sandbox on this computer.");
        // Not in the temporary folder of the user, whose entries are too many for the sandbox to deny each of them: the
        // shell would not start in a folder below it.
        var root = Directory.CreateDirectory(Path.Combine(SandboxHostDiscovery.DefaultDataRoot(), "tests", "worker-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        var secret = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codealta-sandbox-test-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(secret, "the-secret-of-the-user");
        Environment.SetEnvironmentVariable("CODEALTA_TEST_SANDBOX_SECRET", "must-not-reach-the-sandbox");
        try
        {
            var policy = SandboxPolicyBuilder.Build(SandboxProfiles.Resolve(SandboxProfiles.Strict, []), new SandboxSessionFolders { WorkingDirectory = project },
                SandboxHostDiscovery.Current(Path.Combine(root, "data"), []), [], "test");
            var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "altacli.exe" : "altacli");
            var watch = Stopwatch.StartNew();
            await using var worker = await SandboxWorkerClient.StartAsync(backend, policy, $"\"{executable}\" {SandboxWorker.Option}", null,
                new SandboxWorkerInit("session-1", "test", project, [project], null, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false), CancellationToken.None)
                .WaitAsync(Deadline);
            var started = watch.Elapsed;
            var host = new SandboxToolHost((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)), null);

            var written = await worker.InvokeAsync(Call("write_file", new { path = "from-the-sandbox.txt", content = "written" }), host, CancellationToken.None).WaitAsync(Deadline);
            Assert.IsTrue(written.Success, written.Error);
            Assert.AreEqual("written", File.ReadAllText(Path.Combine(project, "from-the-sandbox.txt")));

            var read = await worker.InvokeAsync(Call("read_file", new { path = secret }), host, CancellationToken.None).WaitAsync(Deadline);
            Assert.DoesNotContain("the-secret-of-the-user", Text(read), "the file tools run in the container");

            var command = OperatingSystem.IsWindows() ? $"Get-Content '{secret}'; echo done; $env:CODEALTA_TEST_SANDBOX_SECRET" : $"cat '{secret}'; echo done; echo $CODEALTA_TEST_SANDBOX_SECRET";
            var shell = await worker.InvokeAsync(Call("shell_command", new { command }), host, CancellationToken.None).WaitAsync(Deadline);
            Assert.Contains("done", Text(shell));
            Assert.DoesNotContain("the-secret-of-the-user", Text(shell), "commands run in the container");
            Assert.DoesNotContain("must-not-reach-the-sandbox", Text(shell), "the environment of the application does not reach the sandbox");

            // A command, and a background command, run in the folder of the session.
            var where = OperatingSystem.IsWindows() ? "'shell: ' + (Get-Location).Path" : "echo \"shell: $(pwd)\"";
            var located = await worker.InvokeAsync(Call("shell_command", new { command = where }), host, CancellationToken.None).WaitAsync(Deadline);
            Assert.Contains("shell: " + project, Text(located), StringComparison.OrdinalIgnoreCase);
            var output = new System.Text.StringBuilder();
            var job = worker.StartProcess(where, project, text => { lock (output) output.Append(text); });
            Assert.AreEqual(0, await job.Completion.WaitAsync(Deadline));
            lock (output) Assert.Contains(project, output.ToString(), StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"The worker was ready in {started.TotalMilliseconds:N0} ms.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_SANDBOX_SECRET", null);
            File.Delete(secret);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static AgentToolInvocation Call(string tool, object arguments)
        => new(new ModelProviderId("test"), "session-1", Guid.NewGuid().ToString("N"), tool, JsonSerializer.SerializeToElement(arguments));

    private static string Text(AgentToolResult result) => (result.Error ?? string.Empty) + string.Concat(result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));
}
