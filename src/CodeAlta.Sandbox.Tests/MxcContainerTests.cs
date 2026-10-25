using System.Text;
using CodeAlta.Sandbox.Mxc;

namespace CodeAlta.Sandbox.Tests;

// Real containers: skipped where this computer has no sandbox, as the browser tests are without Edge.
[TestClass]
public sealed class MxcContainerTests
{
    [TestMethod]
    public async Task AProgram_WritesItsProject_AndCannotReadTheHomeFolder()
    {
        var backend = new MxcSandboxBackend();
        if (backend.GetAvailability() is { IsAvailable: false } availability) Assert.Inconclusive(availability.Reason ?? "No sandbox on this computer.");
        using var temp = new TempTree();
        var project = temp.Folder("project");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var secret = Path.Combine(home, ".codealta-sandbox-test-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(secret, "secret");
        try
        {
            var policy = SandboxPolicyBuilder.Build(SandboxProfiles.Resolve(SandboxProfiles.Strict, []), new SandboxSessionFolders { WorkingDirectory = project },
                SandboxHostDiscovery.Current(temp.Folder("data"), []), [], "test");
            var command = OperatingSystem.IsWindows()
                ? $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /c \"echo in> written.txt & type written.txt & type \"{secret}\"\""
                : $"/bin/sh -c \"echo in > written.txt; cat written.txt; cat '{secret}'\"";

            await using var process = await backend.StartAsync(policy, command, null, CancellationToken.None);
            var output = new StreamReader(process.StandardOutput, Encoding.UTF8).ReadToEndAsync();
            var errors = new StreamReader(process.StandardError, Encoding.UTF8).ReadToEndAsync();
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));

            Assert.Contains("in", await output, await errors);
            Assert.DoesNotContain("secret", await output, "the home folder is outside the sandbox");
            Assert.IsTrue(File.Exists(Path.Combine(project, "written.txt")));
        }
        finally
        {
            File.Delete(secret);
        }
    }
}
