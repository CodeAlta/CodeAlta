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
        var windows = OperatingSystem.IsWindows();
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
            var executable = Path.Combine(AppContext.BaseDirectory, windows ? "altacli.exe" : "altacli");
            await using var worker = await SandboxWorkerClient.StartAsync(backend, policy, $"\"{executable}\" {SandboxWorker.Option}", proxy.Endpoint,
                new SandboxWorkerInit("session-1", "test", project, [project], null, home, false), CancellationToken.None).WaitAsync(Deadline);
            var host = new SandboxToolHost((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)), null);
            async Task<string> Run(string command)
                => Text(await worker.InvokeAsync(Call("shell_command", new { command }), host, CancellationToken.None).WaitAsync(Deadline));
            async Task<string> Tool(string tool, object arguments)
                => Text(await worker.InvokeAsync(Call(tool, arguments), host, CancellationToken.None).WaitAsync(Deadline));

            foreach (var secret in secrets)
            {
                Assert.DoesNotContain("the-secret-of-the-user", await Run(windows ? $"Get-Content -LiteralPath '{secret}'" : $"cat '{secret}'"), secret);
                Assert.DoesNotContain("the-secret-of-the-user", await Tool("read_file", new { path = secret }), secret);
            }

            if (linked)
            {
                Assert.DoesNotContain("the-secret-of-the-user", await Run(windows ? "Get-Content -LiteralPath link-to-secret" : "cat link-to-secret"), "a link in the project");
                Assert.DoesNotContain("the-secret-of-the-user", await Tool("read_file", new { path = "link-to-secret" }), "a link in the project, read by a tool");
            }

            // What runs programs later stays as it is.
            foreach (var path in new[] { ".git/hooks/pre-commit", ".git/config", ".alta/plugins/x/plugin.cs", ".vscode/tasks.json" })
            {
                await Run(windows ? $"New-Item -ItemType Directory -Force (Split-Path '{path}') | Out-Null; Set-Content -LiteralPath '{path}' -Value evil"
                    : $"mkdir -p \"$(dirname '{path}')\"; echo evil > '{path}'");
                var full = Path.Combine(project, path);
                Assert.IsFalse(File.Exists(full) && File.ReadAllText(full).Contains("evil", StringComparison.Ordinal), path);
            }

            Assert.DoesNotContain("must-not-reach-the-sandbox", await Run(windows ? "Get-ChildItem env: | Out-String -Width 400" : "env"));
            if (windows)
            {
                // CodeAlta's own port, directly and through the proxy; a host the profile does not list. (On macOS and
                // Linux, the test of the network below.)
                Assert.Contains("refused-direct", await Run($"try {{ $c = [Net.Sockets.TcpClient]::new(); $c.Connect('127.0.0.1', {reserved}); 'connected-direct' }} catch {{ 'refused-direct' }}"));
                Assert.Contains("403", await Run($"try {{ Invoke-WebRequest -Uri 'http://127.0.0.1:{reserved}/mcp' -UseBasicParsing -TimeoutSec 20 | Out-Null; 'reached' }} catch {{ $_.Exception.Response.StatusCode.value__ }}"));
                // For HTTPS, the 403 of the proxy is the failure of the tunnel: the message names it.
                var unlisted = await Run("try { Invoke-WebRequest -Uri 'https://example.com/' -Method Head -UseBasicParsing -TimeoutSec 20 | Out-Null; 'reached' } catch { $_.Exception.Message }");
                Assert.DoesNotContain("reached", unlisted);
                Assert.Contains("403", unlisted);
            }

            // A process started in the background ends with the worker.
            var started = await Run(windows ? "(Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 600' -PassThru).Id" : "nohup sleep 600 >/dev/null 2>&1 & echo $!");
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

    [TestMethod]
    public async Task TheNetworkOfAProfileWithHosts_GoesThroughTheProxyAlone_OnMacOSAndLinux()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("The test above covers Windows.");
        var backend = new MxcSandboxBackend();
        if (backend.GetAvailability() is { } availability && (!availability.IsAvailable || !availability.Networks.Contains(SandboxNetwork.Hosts)))
            Assert.Inconclusive(availability.Reason ?? "No sandbox with hosts on this computer.");
        var root = Directory.CreateDirectory(Path.Combine(SandboxHostDiscovery.DefaultDataRoot(), "tests", "network-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        using var codealta = new TcpListener(IPAddress.Loopback, 0);
        codealta.Start();
        var reserved = ((IPEndPoint)codealta.LocalEndpoint).Port;
        try
        {
            var policy = SandboxPolicyBuilder.Build(SandboxProfiles.Resolve(SandboxProfiles.Workspace, []), new SandboxSessionFolders { WorkingDirectory = project },
                SandboxHostDiscovery.Current(Path.Combine(root, "data"), []), [], "network");
            await using var proxy = SandboxNetworkProxy.Start(policy.Hosts, policy.LocalPorts, new HashSet<int> { reserved });
            await using var worker = await SandboxWorkerClient.StartAsync(backend, policy, $"\"{Path.Combine(AppContext.BaseDirectory, "altacli")}\" {SandboxWorker.Option}", proxy.Endpoint,
                new SandboxWorkerInit("session-1", "test", project, [project], null, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false), CancellationToken.None).WaitAsync(Deadline);
            var host = new SandboxToolHost((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)), null);
            async Task<string> Bash(string script)
                => Text(await worker.InvokeAsync(Call("shell_command", new { command = $"bash -c '{script}'" }), host, CancellationToken.None).WaitAsync(Deadline));
            // The status line of the proxy for a tunnel to a target, through the proxy the container is given.
            string Connect(string target)
                => $"p=${{HTTPS_PROXY:-$HTTP_PROXY}}; p=${{p#http://}}; p=${{p%/}}; exec 3<>/dev/tcp/${{p%:*}}/${{p##*:}} && printf \"CONNECT {target} HTTP/1.1\\r\\nHost: {target}\\r\\n\\r\\n\" >&3 && head -c 12 <&3";

            Assert.Contains("refused-direct", await Bash($"(exec 3<>/dev/tcp/127.0.0.1/{reserved}) 2>/dev/null && echo connected-direct || echo refused-direct"),
                "a port of this computer, directly");
            Assert.Contains("403", await Bash(Connect($"127.0.0.1:{reserved}")), "a port of this computer, through the proxy");
            Assert.Contains("403", await Bash(Connect("example.com:443")), "a host the profile does not list");
            Assert.Contains("200", await Bash(Connect("api.nuget.org:443")), "a host of the profile");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    public async Task TheNetworkThatIsOn_ReachesTheInternet_NotTheNetworkOfThisComputer()
    {
        var backend = new MxcSandboxBackend();
        if (backend.GetAvailability() is { } availability && (!availability.IsAvailable || !availability.Networks.Contains(SandboxNetwork.On)))
            Assert.Inconclusive(availability.Reason ?? "No sandbox with a network on this computer.");
        if (System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(static adapter => adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(static adapter => adapter.GetIPProperties().UnicastAddresses).Select(static unicast => unicast.Address)
                .FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && SandboxNetworkProxy.IsLocalAddress(address))
            is not { } local)
        {
            Assert.Inconclusive("This computer has no address of a local network.");
            return;
        }

        var root = Directory.CreateDirectory(Path.Combine(SandboxHostDiscovery.DefaultDataRoot(), "tests", "network-on-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        using var service = new TcpListener(IPAddress.Any, 0);
        service.Start();
        var port = ((IPEndPoint)service.LocalEndpoint).Port;
        try
        {
            var profile = SandboxProfiles.Resolve(SandboxProfiles.Workspace, []) with { Network = SandboxNetwork.On };
            var policy = SandboxPolicyBuilder.Build(profile, new SandboxSessionFolders { WorkingDirectory = project }, SandboxHostDiscovery.Current(Path.Combine(root, "data"), []), [], "network-on");
            await using var proxy = policy.ThroughProxy ? SandboxNetworkProxy.Start(policy.ProxyHosts, policy.LocalPorts, new HashSet<int>()) : null;
            var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "altacli.exe" : "altacli");
            await using var worker = await SandboxWorkerClient.StartAsync(backend, policy, $"\"{executable}\" {SandboxWorker.Option}", proxy?.Endpoint,
                new SandboxWorkerInit("session-1", "test", project, [project], null, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false), CancellationToken.None).WaitAsync(Deadline);
            var host = new SandboxToolHost((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)), null);
            async Task<string> Connect(string address, int to)
            {
                var command = OperatingSystem.IsWindows()
                    ? $"try {{ $c = [Net.Sockets.TcpClient]::new(); if ($c.ConnectAsync('{address}', {to}).Wait(10000)) {{ 'connected' }} else {{ 'refused' }} }} catch {{ 'refused' }}"
                    // macOS has no `timeout`: without it the connection is still tried, and the test waits for it.
                    : $"$(command -v timeout >/dev/null && echo timeout 10) bash -c '(exec 3<>/dev/tcp/{address}/{to}) 2>/dev/null' && echo connected || echo refused";
                return Text(await worker.InvokeAsync(Call("shell_command", new { command }), host, CancellationToken.None).WaitAsync(Deadline));
            }

            // On macOS a network that is on goes through the proxy, which programs reach by its variables.
            if (!policy.ThroughProxy) Assert.Contains("connected", await Connect("api.nuget.org", 443), "the internet");
            Assert.Contains("refused", await Connect(local.ToString(), port), "a service of this computer on its local address");
        }
        finally
        {
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
