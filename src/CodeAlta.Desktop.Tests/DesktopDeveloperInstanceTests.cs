using System.Collections.Frozen;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopDeveloperInstanceTests
{
    [TestMethod]
    public void Cli_DevAloneStartsTheDeveloperInstanceOnTheSharedProfile()
    {
        Assert.IsTrue(DesktopCommandLine.TryParse([], _ => false, _ => false, out var normal, out _));
        Assert.IsTrue(DesktopCommandLine.TryParse(["--dev"], _ => false, _ => false, out var developer, out var error));

        Assert.IsNull(error);
        Assert.IsFalse(normal!.Developer);
        Assert.IsNull(normal.StateRoot);
        Assert.IsTrue(developer!.Developer);
        // Same profile, own state and own WebView data: the two run side by side.
        Assert.AreEqual(normal.CatalogRoot, developer.CatalogRoot);
        Assert.AreEqual(Path.Combine(normal.CatalogRoot!, "dev"), developer.StateRoot);
        Assert.AreNotEqual(normal.DataRoot, developer.DataRoot);
        StringAssert.EndsWith(developer.DataRoot, "desktop-dev");
        Assert.AreEqual(normal.Owned, developer.Owned);
    }

    [TestMethod]
    public void Cli_DevCannotBeCombinedWithOtherOptions()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        Assert.IsFalse(DesktopCommandLine.TryParse(["--dev", "--dev"], _ => false, _ => false, out _, out _));
        Assert.IsFalse(DesktopCommandLine.TryParse(["--dev", "--data-root", Path.Combine(root, "dev-fixture-browser")], _ => false, _ => false, out _, out _));
        Assert.IsFalse(DesktopCommandLine.TryParse(["--data-root", Path.Combine(root, "dev-fixture-browser"), "--dev"], _ => false, _ => false, out _, out _));
    }

    [TestMethod]
    public void BootStatus_TellsThePageItIsTheDeveloperInstance()
    {
        const string epoch = "11111111-1111-4111-8111-111111111111";
        Assert.IsTrue(new BootService(epoch, false, false, true).Status(new()).DeveloperMode);
        Assert.IsFalse(new BootService(epoch, false, false, false).Status(new()).DeveloperMode);
        Assert.IsFalse(new BootService(epoch, false, false).Status(new()).DeveloperMode);
        Assert.AreEqual("CodeAlta (dev)", DesktopWindowChrome.WindowOptions(developer: true).Title);
        Assert.AreEqual("CodeAlta", DesktopWindowChrome.WindowOptions().Title);
    }

    [TestMethod]
    public async Task Host_WithASeparateStateRootKeepsSessionsThereAndLeavesSharedInstructionsAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-dev-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var global = Path.Combine(root, "global"); var project = Path.Combine(root, "project");
            var home = Path.Combine(root, "home"); var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, project, home, builtin }) Directory.CreateDirectory(path);
            var instructions = Path.Combine(global, "AGENTS.md");
            await File.WriteAllTextAsync(instructions, "maintained by the instance that owns this root");
            var state = Path.Combine(global, "dev");

            await using (var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, StateRoot = state, CurrentProjectPath = project, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }))
            {
                Assert.AreEqual(global, host.CatalogOptions.GlobalRoot);
                Assert.AreEqual(state, host.CatalogOptions.StateRoot);
                Assert.AreEqual(Path.Combine(state, "sessions"), host.CatalogOptions.SessionsRoot);
                Assert.IsTrue(Directory.Exists(state));
            }

            Assert.AreEqual("maintained by the instance that owns this root", await File.ReadAllTextAsync(instructions));

            // The owner of the root keeps the same file current.
            await using (await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = project, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            })) { }
            Assert.AreNotEqual("maintained by the instance that owns this root", await File.ReadAllTextAsync(instructions));

            // A developer instance on a profile without instructions supplies them.
            File.Delete(instructions);
            await using (await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, StateRoot = state, CurrentProjectPath = project, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            })) { }
            Assert.IsTrue(File.Exists(instructions));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }
}
