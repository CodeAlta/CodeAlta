using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Sandbox;
using CodeAlta.Sandbox.Mxc;
using CodeAlta.Sandbox.Network;
using CodeAlta.Sandbox.Worker;
using Microsoft.Mxc.Sdk.V1;

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
        // Beside the project, and through a link to its folder, which stays in the sandbox as the links of a Windows
        // profile do (`Local Settings` names AppData\Local).
        var besideTheProject = Path.Combine(root, "beside-the-project.txt");
        File.WriteAllText(besideTheProject, "the-secret-of-the-user");
        var toTheParent = Path.Combine(root, "to-the-parent");
        if (windows) Junction(toTheParent, root);
        // A folder at the root of the disk, which the sandbox must not create; removed at the end if it did.
        var outside = Path.Combine(Path.GetPathRoot(project)!, "codealta-escape-" + Guid.NewGuid().ToString("N")[..8]);
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

            // Relative paths resolve from the folder the shell is really in, which the result of a command does not say
            // (its `working_directory` is the folder asked for). Checked before they are written: elsewhere, the checks
            // below would not see them, and they would stay on the disk.
            Assert.Contains("shell: " + project, await Run(windows ? "'shell: ' + (Get-Location).Path" : "echo \"shell: $(pwd)\""), StringComparison.OrdinalIgnoreCase);

            if (windows)
            {
                var throughTheLink = Path.Combine(toTheParent, Path.GetFileName(besideTheProject));
                Assert.DoesNotContain("the-secret-of-the-user", await Run($"Get-Content -LiteralPath '{besideTheProject}'"), "a file beside the project");
                Assert.DoesNotContain("the-secret-of-the-user", await Run($"Get-Content -LiteralPath '{throughTheLink}'"), "a file beside the project, through a link to its folder");
                await Run($"Set-Content -LiteralPath '{Path.Combine(toTheParent, "evil.txt")}' -Value evil");
                Assert.IsFalse(File.Exists(Path.Combine(root, "evil.txt")), "a file beside the project, written through a link to its folder");

                // A Windows that cannot only list a volume root keeps it read-write, which lets the sandbox create this folder
                // (doc/sandbox.md).
                if (MxcPlatform.GetAvailableBackends().Any(static item => item.Backend == ContainmentBackend.ProcessContainer && item.Capabilities.Contains(BackendCapability.FilesystemEnumeratePaths)))
                {
                    await Run($"New-Item -ItemType Directory -Path '{outside}' | Out-Null; Set-Content -LiteralPath '{Path.Combine(outside, "evil.txt")}' -Value evil");
                    Assert.IsFalse(Directory.Exists(outside), "a folder at the root of the disk");
                }
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
                // Linux, the test of the network below.) The proxy is asked directly, for every shell: the web commands of
                // Windows PowerShell read no proxy variable and send no loopback address through a proxy.
                string AskTheProxy(string request, string target)
                    => $"$u = [Uri]$env:HTTP_PROXY; $c = [Net.Sockets.TcpClient]::new($u.Host, $u.Port); $s = $c.GetStream(); " +
                       $"$b = [Text.Encoding]::ASCII.GetBytes(\"{request} HTTP/1.1`r`nHost: {target}`r`n`r`n\"); $s.Write($b, 0, $b.Length); " +
                       "$r = [byte[]]::new(64); $n = $s.Read($r, 0, 64); [Text.Encoding]::ASCII.GetString($r, 0, $n)";
                Assert.Contains("refused-direct", await Run($"try {{ $c = [Net.Sockets.TcpClient]::new(); $c.Connect('127.0.0.1', {reserved}); 'connected-direct' }} catch {{ 'refused-direct' }}"));
                Assert.Contains("HTTP/1.1 403", await Run(AskTheProxy($"GET http://127.0.0.1:{reserved}/mcp", $"127.0.0.1:{reserved}")));
                Assert.Contains("HTTP/1.1 403", await Run(AskTheProxy("CONNECT example.com:443", "example.com:443")));
            }

            // A process started in the background ends with the worker.
            var started = await Run(windows ? "(Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 600' -PassThru).Id" : "nohup sleep 600 >/dev/null 2>&1 & echo $!");
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
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
            // Alone first: a recursive delete refuses a junction without the rights of an administrator.
            if (Directory.Exists(toTheParent)) Directory.Delete(toTheParent);
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

        // On Windows a service on a local network address makes the firewall ask the user to let the test host through,
        // again for each folder the tests run from: the test runs there only when asked.
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CODEALTA_TEST_LOCAL_NETWORK") != "1")
        {
            Assert.Inconclusive("Set CODEALTA_TEST_LOCAL_NETWORK=1 to listen on a local network address, which the Windows firewall asks about.");
            return;
        }

        var root = Directory.CreateDirectory(Path.Combine(SandboxHostDiscovery.DefaultDataRoot(), "tests", "network-on-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        using var service = new TcpListener(local, 0);
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

    private static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);
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
