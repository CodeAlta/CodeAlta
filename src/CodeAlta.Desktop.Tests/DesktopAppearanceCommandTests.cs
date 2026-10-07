using System.Text.Json;
using CodeAlta.Desktop;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopAppearanceCommandTests
{
    [TestMethod]
    public async Task AltaAppearance_ChangesTheViewOfOneSession_AndLeavesTheSettingOfTheUser()
    {
        var appearance = new Appearance { SessionWidth = 80 };
        var services = new AltaServiceCollection().Add<IAltaAppearance>(appearance);
        var registry = new AltaCommandRegistry();
        var alta = new AltaCommandDispatcher(registry, services);
        services.Add(registry).Add(alta);
        var first = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1" };
        var second = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-2" };
        StringAssert.Contains((await alta.InvokeAsync(["--help"])).Stdout, "appearance");
        var tools = (await alta.InvokeAsync(["tool", "list"])).Stdout;
        StringAssert.Contains(tools, "appearance get");
        StringAssert.Contains(tools, "appearance set");

        // A session that has no width of its own is shown with the setting of the user.
        var shown = Record(await alta.InvokeAsync(["appearance", "get"], caller: first), "alta.appearance");
        Assert.AreEqual((80, "user", 80, "session-1"), (shown.GetProperty("sessionWidth").GetInt32(), shown.GetProperty("sessionWidthSource").GetString(),
            shown.GetProperty("userSessionWidth").GetInt32(), shown.GetProperty("sessionId").GetString()));
        Assert.AreEqual((40, 100, "percent"), (shown.GetProperty("sessionWidthMinimum").GetInt32(), shown.GetProperty("sessionWidthMaximum").GetInt32(), shown.GetProperty("sessionWidthUnit").GetString()));

        // A change is for the calling session: the setting and the other sessions keep theirs.
        var changed = Record(await alta.InvokeAsync(["appearance", "set", "--session-width", "60%"], caller: first), "alta.appearance.changed");
        Assert.AreEqual((60, "session", 80), (changed.GetProperty("sessionWidth").GetInt32(), changed.GetProperty("sessionWidthSource").GetString(), changed.GetProperty("userSessionWidth").GetInt32()));
        Assert.AreEqual(80, appearance.SessionWidth);
        Assert.AreEqual(60, appearance.GetSessionWidth("session-1"));
        var other = Record(await alta.InvokeAsync(["appearance", "get"], caller: second), "alta.appearance");
        Assert.AreEqual((80, "user"), (other.GetProperty("sessionWidth").GetInt32(), other.GetProperty("sessionWidthSource").GetString()));
        // Another session can be named; a caller that is no session has to name one.
        Assert.AreEqual(AltaExitCodes.Success, (await alta.InvokeAsync(["appearance", "set", "--session", "session-3", "--session-width", "45"], caller: second)).ExitCode);
        Assert.AreEqual(45, appearance.GetSessionWidth("session-3"));
        Assert.IsNull(appearance.GetSessionWidth("session-2"));
        var nobody = await alta.InvokeAsync(["appearance", "set", "--session-width", "70"], caller: new AltaCallerIdentity { Kind = "mcp" });
        Assert.AreEqual(AltaExitCodes.Usage, nobody.ExitCode);
        StringAssert.Contains(nobody.Stdout + nobody.Stderr, "usage.missingSession");
        Assert.AreEqual("user", Record(await alta.InvokeAsync(["appearance", "get"], caller: new AltaCallerIdentity { Kind = "mcp" }), "alta.appearance").GetProperty("sessionWidthSource").GetString());

        // `default` follows the setting of the user again.
        var back = Record(await alta.InvokeAsync(["appearance", "set", "--session-width", "default"], caller: first), "alta.appearance.changed");
        Assert.AreEqual((80, "user"), (back.GetProperty("sessionWidth").GetInt32(), back.GetProperty("sessionWidthSource").GetString()));
        Assert.IsNull(appearance.GetSessionWidth("session-1"));

        // What is no width changes nothing and says what a width is.
        foreach (var bad in new[] { "39", "101", "wide", "-50", "50.5", "" })
        {
            var refused = await alta.InvokeAsync(["appearance", "set", "--session-width", bad], caller: first);
            Assert.AreEqual(AltaExitCodes.Usage, refused.ExitCode, bad);
            Assert.IsNull(appearance.GetSessionWidth("session-1"), bad);
        }

        Assert.AreEqual(AltaExitCodes.Usage, (await alta.InvokeAsync(["appearance", "set"], caller: first)).ExitCode);

        // A host without a window has no such command.
        var bare = new AltaServiceCollection();
        var bareRegistry = new AltaCommandRegistry();
        var without = new AltaCommandDispatcher(bareRegistry, bare);
        bare.Add(bareRegistry).Add(without);
        Assert.IsFalse((await without.InvokeAsync(["tool", "list"])).Stdout.Contains("appearance", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Preferences_KeepTheWidthOfTheConversations_WhenItIsNotTheWholeSpace()
    {
        var root = Directory.CreateTempSubdirectory("codealta-width-").FullName;
        try
        {
            var file = Path.Combine(root, "preferences.json");
            Assert.AreEqual(100, DesktopPreferences.Load(root).SessionWidth);
            Assert.IsTrue(new DesktopPreferences(DesktopCloseBehavior.KeepRunning, SessionWidth: 70).Save(root));
            Assert.AreEqual("""{"onClose":"keep","sessionWidth":70}""", File.ReadAllText(file));
            Assert.AreEqual(new DesktopPreferences(DesktopCloseBehavior.KeepRunning, true, 70), DesktopPreferences.Load(root));
            // The whole space is the default: it is not written.
            Assert.IsTrue(new DesktopPreferences(DesktopCloseBehavior.Exit, McpServer: false, SessionWidth: 100).Save(root));
            Assert.AreEqual("""{"onClose":"exit","mcpServer":false}""", File.ReadAllText(file));
            // A width that is none gives the whole space, whatever else the file says.
            foreach (var bad in new[] { "39", "101", "\"70\"", "70.5", "null" })
            {
                File.WriteAllText(file, $$"""{"onClose":"keep","sessionWidth":{{bad}}}""");
                Assert.AreEqual(new DesktopPreferences(DesktopCloseBehavior.KeepRunning), DesktopPreferences.Load(root), bad);
            }

            File.WriteAllText(file, """{"closeToTray":false,"sessionWidth":55}""");
            Assert.AreEqual(new DesktopPreferences(DesktopCloseBehavior.Exit, true, 55), DesktopPreferences.Load(root));
            File.WriteAllText(file, """{"sessionWidth":40}""");
            Assert.AreEqual(new DesktopPreferences(DesktopCloseBehavior.Ask, true, 40), DesktopPreferences.Load(root));
            Assert.IsTrue(DesktopPreferences.IsSessionWidth(40) && DesktopPreferences.IsSessionWidth(100) && !DesktopPreferences.IsSessionWidth(39) && !DesktopPreferences.IsSessionWidth(101));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static JsonElement Record(AltaCommandResult result, string type)
    {
        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("type", out var name) && name.GetString() == type) return document.RootElement.Clone();
        }

        Assert.Fail($"No {type} record in: {result.Stdout}");
        return default;
    }

    private sealed class Appearance : IAltaAppearance
    {
        private readonly Dictionary<string, int> _own = new(StringComparer.OrdinalIgnoreCase);

        public int SessionWidth { get; set; }

        public int? GetSessionWidth(string sessionId) => _own.TryGetValue(sessionId, out var value) ? value : null;

        public bool SetSessionWidth(string sessionId, int? percent)
        {
            if (percent is { } value)
            {
                if (value is < IAltaAppearance.MinimumSessionWidth or > IAltaAppearance.DefaultSessionWidth) return false;
                _own[sessionId] = value;
            }
            else
            {
                _own.Remove(sessionId);
            }

            return true;
        }
    }
}
