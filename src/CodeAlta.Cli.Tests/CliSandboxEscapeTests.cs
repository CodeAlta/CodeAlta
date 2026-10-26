using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Sandbox;
using CodeAlta.Sandbox.Mxc;
using CodeAlta.Sandbox.Network;
using CodeAlta.Sandbox.Worker;

namespace CodeAlta.Cli.Tests;

// What a sandboxed session must not reach, tried from inside a real container: the secrets of the user, what runs
// programs later, CodeAlta's own port, the hosts its profile does not list, the variables of the application, and a
// process left behind. Skipped where this computer has no sandbox.
[TestClass]
public sealed class CliSandboxEscapeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    [TestMethod]
    public async Task NothingOutsideTheSandbox_IsReachedFromInside()
    {
        var backend = new MxcSandboxBackend();
        if (backend.GetAvailability() is { IsAvailable: false } availability) Assert.Inconclusive(availability.Reason ?? "No sandbox on this computer.");
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("The commands of this test are those of PowerShell.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Directory.CreateDirectory(Path.Combine(SandboxHostDiscovery.DefaultDataRoot(), "tests", "escape-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git", "hooks"));
        File.WriteAllText(Path.Combine(project, ".git", "config"), "[core]\n");
        // Secrets of the user where CodeAlta and ssh keep theirs; a test file in each, removed at the end.
        var secrets = new[] { Path.Combine(home, ".alta", "auth"), Path.Combine(home, ".ssh") }
            .Select(folder => Path.Combine(Directory.CreateDirectory(folder).FullName, "codealta-escape-" + Guid.NewGuid().ToString("N")[..8])).ToArray();
        foreach (var secret in secrets) File.WriteAllText(secret, "the-secret-of-the-user");
        var link = Path.Combine(project, "link-to-secret");
        var linked = TryLink(link, secrets[0]);
        Environment.SetEnvironmentVariable("CODEALTA_ESCAPE_API_KEY", "must-not-reach-the-sandbox");
        using var codealta = new TcpListener(IPAddress.Loopback, 0);
        codealta.Start();
        var reserved = ((IPEndPoint)codealta.LocalEndpoint).Port;
        try
        {
            var policy = SandboxPolicyBuilder.Build(SandboxProfiles.Resolve(SandboxProfiles.Workspace, []), new SandboxSessionFolders { WorkingDirectory = project },
                SandboxHostDiscovery.Current(Path.Combine(root, "data"), []), [], "escape");
            await using var proxy = SandboxNetworkProxy.Start(policy.Hosts, policy.LocalPorts, new HashSet<int> { reserved });
            var executable = Path.Combine(AppContext.BaseDirectory, "altacli.exe");
            await using var worker = await SandboxWorkerClient.StartAsync(backend, policy, $"\"{executable}\" {SandboxWorker.Option}", proxy.Endpoint,
                new SandboxWorkerInit("session-1", "test", project, [project], null, home, false), CancellationToken.None).WaitAsync(Deadline);
            var host = new SandboxToolHost((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)), null);
            async Task<string> Run(string command)
                => Text(await worker.InvokeAsync(Call("shell_command", new { command }), host, CancellationToken.None).WaitAsync(Deadline));
            async Task<string> Tool(string tool, object arguments)
                => Text(await worker.InvokeAsync(Call(tool, arguments), host, CancellationToken.None).WaitAsync(Deadline));

            foreach (var secret in secrets)
            {
                Assert.DoesNotContain("the-secret-of-the-user", await Run($"Get-Content -LiteralPath '{secret}'"), secret);
                Assert.DoesNotContain("the-secret-of-the-user", await Tool("read_file", new { path = secret }), secret);
            }

            if (linked)
            {
                Assert.DoesNotContain("the-secret-of-the-user", await Run("Get-Content -LiteralPath link-to-secret"), "a link in the project");
                Assert.DoesNotContain("the-secret-of-the-user", await Tool("read_file", new { path = "link-to-secret" }), "a link in the project, read by a tool");
            }

            // What runs programs later stays as it is.
            foreach (var path in new[] { ".git/hooks/pre-commit", ".git/config", ".alta/plugins/x/plugin.cs", ".vscode/tasks.json" })
            {
                await Run($"New-Item -ItemType Directory -Force (Split-Path '{path}') | Out-Null; Set-Content -LiteralPath '{path}' -Value evil");
                var full = Path.Combine(project, path);
                Assert.IsFalse(File.Exists(full) && File.ReadAllText(full).Contains("evil", StringComparison.Ordinal), path);
            }

            // CodeAlta's own port, directly and through the proxy; a host the profile does not list.
            Assert.Contains("refused-direct", await Run($"try {{ $c = [Net.Sockets.TcpClient]::new(); $c.Connect('127.0.0.1', {reserved}); 'connected-direct' }} catch {{ 'refused-direct' }}"));
            Assert.Contains("403", await Run($"try {{ Invoke-WebRequest -Uri 'http://127.0.0.1:{reserved}/mcp' -UseBasicParsing -TimeoutSec 20 | Out-Null; 'reached' }} catch {{ $_.Exception.Response.StatusCode.value__ }}"));
            // For HTTPS, the 403 of the proxy is the failure of the tunnel: the message names it.
            var unlisted = await Run("try { Invoke-WebRequest -Uri 'https://example.com/' -Method Head -UseBasicParsing -TimeoutSec 20 | Out-Null; 'reached' } catch { $_.Exception.Message }");
            Assert.DoesNotContain("reached", unlisted);
            Assert.Contains("403", unlisted);
            Assert.DoesNotContain("must-not-reach-the-sandbox", await Run("Get-ChildItem env: | Out-String -Width 400"));

            // A process started in the background ends with the worker.
            var started = await Run("(Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 600' -PassThru).Id");
            var id = int.Parse(started.Split('\n').Select(static line => line.Trim()).First(static line => line.Length > 0 && line.All(char.IsAsciiDigit)), System.Globalization.CultureInfo.InvariantCulture);
            await worker.DisposeAsync();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(10) && IsRunning(id)) await Task.Delay(200);
            Assert.IsFalse(IsRunning(id), "the process left behind ended with the container");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_ESCAPE_API_KEY", null);
            foreach (var secret in secrets) File.Delete(secret);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static AgentToolInvocation Call(string tool, object arguments)
        => new(new ModelProviderId("test"), "session-1", Guid.NewGuid().ToString("N"), tool, JsonSerializer.SerializeToElement(arguments));

    private static string Text(AgentToolResult result) => (result.Error ?? string.Empty) + string.Concat(result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));
}
