using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Documentation;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.Tests;

/// <summary>Tests of <c>alta documentation</c>: the user guide that ships with the application, listed, read, searched and shown by agents.</summary>
[TestClass]
public sealed class AltaDocumentationCommandsTests
{
    [TestMethod]
    public async Task DocumentationGroup_ExistsWithAGuide_AndOpenOnlyWithAView()
    {
        using var none = Fixture.Create(guide: false, view: false);
        Assert.AreEqual(AltaExitCodes.Usage, (await none.RunAsync("documentation", "list")).ExitCode);
        Assert.IsFalse((await none.RunAsync("tool", "list")).Stdout.Contains("documentation", StringComparison.Ordinal));

        using var reader = Fixture.Create(view: false);
        var tools = (await reader.RunAsync("tool", "list")).Stdout;
        foreach (var leaf in new[] { "list", "read", "search" })
        {
            StringAssert.Contains(tools, $"documentation {leaf}");
            Assert.IsTrue((await reader.RunAsync("documentation", leaf, "--help")).IsHelp, leaf);
        }

        Assert.IsFalse(tools.Contains("documentation open", StringComparison.Ordinal));
        Assert.AreEqual(AltaExitCodes.Usage, (await reader.RunAsync("documentation", "open")).ExitCode);

        using var window = Fixture.Create();
        StringAssert.Contains((await window.RunAsync("tool", "list")).Stdout, "documentation open");
        Assert.IsTrue((await window.RunAsync("documentation", "open", "--help")).IsHelp);
        StringAssert.Contains((await window.RunAsync("documentation", "--help")).Stdout, "The guide says what CodeAlta does");
        // The policies say that nothing here changes anything.
        var policies = Records(await window.RunAsync("tool", "list", "--detailed")).Where(static line => line.TryGetProperty("path", out var path) && path.GetString()!.StartsWith("documentation ", StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(4, policies.Length);
        Assert.IsTrue(policies.All(static policy => !policy.GetProperty("isMutating").GetBoolean() && !policy.GetProperty("isDisruptive").GetBoolean()));
    }

    [TestMethod]
    public async Task ApplicationWithoutAGuide_SaysSo()
    {
        using var fixture = Fixture.Create(empty: true);
        foreach (var args in new[] { new[] { "documentation", "list" }, ["documentation", "read"], ["documentation", "search", "guide"], ["documentation", "open"] })
        {
            await fixture.FailsAsync(AltaExitCodes.ServiceUnavailable, "documentation.unavailable", args);
        }

        Assert.AreEqual(0, fixture.View.Shown.Count);
    }

    [TestMethod]
    public async Task List_PrintsTheMenu_ThenThePagesItDoesNotName()
    {
        using var fixture = Fixture.Create();
        var result = await fixture.OkAsync("documentation", "list");
        var pages = Records(result).Where(static line => Type(line) == "alta.documentation.page").ToArray();
        CollectionAssert.AreEqual(new[] { "readme.md", "sessions.md", "plugins/readme.md", "plugins/git.md", "orphan.md" }, pages.Select(static page => page.GetProperty("page").GetString()).ToArray());
        Assert.AreEqual("User Guide", pages[0].GetProperty("title").GetString());
        Assert.AreEqual(0, pages[2].GetProperty("depth").GetInt32());
        Assert.AreEqual(1, pages[3].GetProperty("depth").GetInt32());
        Assert.AreEqual("plugins/readme.md", pages[3].GetProperty("parent").GetString());
        Assert.IsTrue(pages[3].GetProperty("menu").GetBoolean());
        Assert.IsFalse(pages[4].GetProperty("menu").GetBoolean());
        Assert.IsFalse(pages[0].TryGetProperty("parent", out _));
        var summary = Records(result).Single(static line => Type(line) == "alta.documentation.summary");
        Assert.AreEqual(5, summary.GetProperty("count").GetInt32());
        Assert.AreEqual(fixture.GuideRoot, summary.GetProperty("root").GetString());
        Assert.AreEqual("readme.md", summary.GetProperty("home").GetString());
    }

    [TestMethod]
    public async Task Read_PrintsAPageAsMarkdown_InPartsWhenItIsLong()
    {
        using var fixture = Fixture.Create();
        var home = Records(await fixture.OkAsync("documentation", "read")).Single(static line => Type(line) == "alta.documentation.content");
        Assert.AreEqual("readme.md", home.GetProperty("page").GetString());
        Assert.AreEqual("User Guide", home.GetProperty("title").GetString());
        Assert.AreEqual(Path.Combine(fixture.GuideRoot, "readme.md"), home.GetProperty("file").GetString());
        Assert.IsFalse(home.GetProperty("truncated").GetBoolean());
        Assert.IsFalse(home.TryGetProperty("nextOffset", out _));
        var markdown = home.GetProperty("markdown").GetString()!;
        // The page as the application shows it: the link names the page from the guide, the picture is a file below its folder.
        StringAssert.Contains(markdown, "[Sessions](sessions.md#queue)");
        StringAssert.Contains(markdown, "![The workspace](img/alta-desktop-home.webp)\n\nThe main workspace.");
        Assert.IsFalse(markdown.Contains("{{", StringComparison.Ordinal) || markdown.Contains("title:", StringComparison.Ordinal));

        // A page is named as the list prints it, whatever its case and its separators, or by the full path of its file.
        foreach (var reference in new[] { "plugins/git.md", "Plugins\\Git.md", Path.Combine(fixture.GuideRoot, "plugins", "git.md") })
        {
            var git = Records(await fixture.OkAsync("documentation", "read", reference)).Single(static line => Type(line) == "alta.documentation.content");
            Assert.AreEqual("plugins/git.md", git.GetProperty("page").GetString(), reference);
        }

        var first = Records(await fixture.OkAsync("documentation", "read", "sessions.md", "--limit", "60")).Single(static line => Type(line) == "alta.documentation.content");
        Assert.IsTrue(first.GetProperty("truncated").GetBoolean());
        var part = first.GetProperty("markdown").GetString()!;
        Assert.IsTrue(part.Length <= 60 && part.EndsWith('\n'), part);
        var next = first.GetProperty("nextOffset").GetInt32();
        Assert.AreEqual(part.Length, next);
        var rest = Records(await fixture.OkAsync("documentation", "read", "sessions.md", "--offset", next.ToString(System.Globalization.CultureInfo.InvariantCulture))).Single(static line => Type(line) == "alta.documentation.content");
        Assert.IsFalse(rest.GetProperty("truncated").GetBoolean());
        Assert.AreEqual(first.GetProperty("length").GetInt32(), next + rest.GetProperty("markdown").GetString()!.Length);
        Assert.AreEqual(fixture.Documentation.ReadPage("sessions.md")!.ToMarkdown(), part + rest.GetProperty("markdown").GetString());

        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidOffset", "documentation", "read", "sessions.md", "--offset", "-1");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidOffset", "documentation", "read", "sessions.md", "--offset", "100000");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidLimit", "documentation", "read", "sessions.md", "--limit", "0");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidLimit", "documentation", "read", "sessions.md", "--limit", "1000000");
    }

    [TestMethod]
    public async Task Read_NamesOnlyAPageOfTheGuide()
    {
        using var fixture = Fixture.Create();
        var outside = Path.Combine(fixture.Root, "outside.md");
        File.WriteAllText(outside, "# Outside\n\nsecret");
        foreach (var reference in new[] { "nowhere.md", "../outside.md", outside, "file:///" + outside.Replace('\\', '/'), "menu.yml", "img/alta-desktop-home.webp", "https://example.com/readme.md" })
        {
            var error = await fixture.FailsAsync(AltaExitCodes.NotFound, "documentation.notFound", "documentation", "read", reference);
            Assert.IsFalse(error.GetRawText().Contains("secret", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task Search_FindsATextUnderItsHeading()
    {
        using var fixture = Fixture.Create();
        var result = await fixture.OkAsync("documentation", "search", "prompt QUEUE");
        var hit = Records(result).Single(static line => Type(line) == "alta.documentation.hit");
        Assert.AreEqual("sessions.md", hit.GetProperty("page").GetString());
        Assert.AreEqual("Sessions", hit.GetProperty("title").GetString());
        Assert.AreEqual("Queue", hit.GetProperty("heading").GetString());
        Assert.AreEqual("A session keeps a prompt queue while it runs.", hit.GetProperty("text").GetString());
        Assert.AreEqual(1, Records(result).Single(static line => Type(line) == "alta.documentation.hitSummary").GetProperty("count").GetInt32());
        Assert.AreEqual(0, Records(await fixture.OkAsync("documentation", "search", "nowhere in the guide")).Single(static line => Type(line) == "alta.documentation.hitSummary").GetProperty("count").GetInt32());
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidText", "documentation", "search", "a");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalid", "documentation", "search");
    }

    [TestMethod]
    public async Task Open_AsksTheWindowForAPage_AndSaysWhenNoneIsThere()
    {
        using var fixture = Fixture.Create();
        var opened = Records(await fixture.OkAsync("documentation", "open")).Single(static line => Type(line) == "alta.documentation.opened");
        Assert.IsFalse(opened.TryGetProperty("page", out _));
        opened = Records(await fixture.OkAsync("documentation", "open", "Plugins/git.md", "--anchor", "#sign-in")).Single(static line => Type(line) == "alta.documentation.opened");
        Assert.AreEqual("plugins/git.md", opened.GetProperty("page").GetString());
        Assert.AreEqual("sign-in", opened.GetProperty("anchor").GetString());
        CollectionAssert.AreEqual(new (string?, string?)[] { (null, null), ("plugins/git.md", "sign-in") }, fixture.View.Shown);

        await fixture.FailsAsync(AltaExitCodes.NotFound, "documentation.notFound", "documentation", "open", "../outside.md");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidAnchor", "documentation", "open", "--anchor", "sign-in");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidAnchor", "documentation", "open", "sessions.md", "--anchor", "a b\"]");
        Assert.AreEqual(2, fixture.View.Shown.Count);

        fixture.View.HasWindow = false;
        await fixture.FailsAsync(AltaExitCodes.ServiceUnavailable, "view.unavailable", "documentation", "open", "sessions.md");
    }

    [TestMethod]
    public async Task Plugin_CannotTakeTheDocumentationRoot()
    {
        var ran = false;
        var catalog = new FakePluginCatalog(new AltaPluginCommandContribution
        {
            Plugin = new PluginDescriptor { RuntimeKey = "global:docs", TypeName = "Sample.Plugin", AssemblyName = "Sample.Plugin", DisplayName = "Docs", Version = "1.0.0" },
            Services = NoopPluginServices.Create(),
            Scope = PluginScope.Global,
            Command = new PluginAltaCommandContribution
            {
                Path = "documentation",
                Policy = new PluginAltaCommandPolicy { IsMutating = true },
                CreateCommandNode = _ =>
                {
                    var command = new Command("documentation", "A plugin that takes the name of the user guide.") { new CommandUsage(), new HelpOption() };
                    command.Add((_, _) => { ran = true; return new ValueTask<int>(AltaExitCodes.Success); });
                    return command;
                },
            },
        });
        // With the guide, and in a host that ships none: the name is the application's either way.
        foreach (var guide in new[] { true, false })
        {
            using var fixture = Fixture.Create(guide: guide, plugins: catalog);
            var help = (await fixture.RunAsync("--help")).Stdout;
            Assert.IsFalse(help.Contains("A plugin that takes the name", StringComparison.Ordinal));
            await fixture.RunAsync("documentation");
            Assert.IsFalse(ran);
            Assert.IsFalse(Records(await fixture.RunAsync("tool", "list", "--detailed")).Any(static line => line.TryGetProperty("path", out var path) && path.GetString() == "documentation"));
        }
    }

    private static string? Type(JsonElement line) => line.GetProperty("type").GetString();

    private static List<JsonElement> Records(AltaCommandResult result)
    {
        var values = new List<JsonElement>();
        foreach (var line in result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            values.Add(document.RootElement.Clone());
        }

        return values;
    }

    private sealed class FakeDocumentationView : IAltaDocumentationView
    {
        public bool HasWindow { get; set; } = true;

        public List<(string? Page, string? Anchor)> Shown { get; } = [];

        public bool Show(string? page, string? anchor)
        {
            if (!HasWindow) return false;
            Shown.Add((page, anchor));
            return true;
        }
    }

    private sealed class FakePluginCatalog(params AltaPluginCommandContribution[] contributions) : IAltaPluginCatalog
    {
        public IReadOnlyList<AltaPluginSummary> ListPlugins() => [];

        public AltaPluginSummary? GetPlugin(string runtimeKey) => null;

        public IReadOnlyList<AltaCommandPolicy> ListCommandPolicies() => [];

        public IReadOnlyList<AltaPluginCommandContribution> ListCommandContributions() => contributions;
    }

    private sealed class Fixture : IDisposable
    {
        // The commands are run by a caller that belongs to no session, as the MCP server of the window does.
        private static readonly AltaCallerIdentity Mcp = new() { Kind = "mcp" };

        private Fixture(string root)
        {
            Root = root;
            GuideRoot = Path.Combine(root, "user-guide");
            Documentation = new ShippedDocumentation(GuideRoot);
        }

        public string Root { get; }

        public string GuideRoot { get; }

        public ShippedDocumentation Documentation { get; }

        public FakeDocumentationView View { get; } = new();

        private AltaCommandDispatcher Dispatcher { get; set; } = null!;

        public static Fixture Create(bool guide = true, bool view = true, bool empty = false, IAltaPluginCatalog? plugins = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta.AltaDocumentationCommandsTests", Guid.NewGuid().ToString("N"));
            var fixture = new Fixture(root);
            Directory.CreateDirectory(Path.Combine(root, "home"));
            if (!empty)
            {
                Directory.CreateDirectory(Path.Combine(fixture.GuideRoot, "plugins"));
                Directory.CreateDirectory(Path.Combine(fixture.GuideRoot, "img"));
                File.WriteAllText(Path.Combine(fixture.GuideRoot, "menu.yml"), "doc:\n  - {path: readme.md, title: \"<i class='bi bi-book'></i> User Guide\"}\n  - {path: sessions.md, title: \"Sessions\"}\n  - {path: plugins/readme.md, title: \"Plugins\", folder: true}\n");
                File.WriteAllText(Path.Combine(fixture.GuideRoot, "plugins", "menu.yml"), "doc:\n  - {path: readme.md, title: \"Overview\"}\n  - {path: git.md, title: \"Git\"}\n");
                File.WriteAllText(Path.Combine(fixture.GuideRoot, "readme.md"), "---\ntitle: User Guide\n---\n\n# User Guide\n\nSee [Sessions]({{site.basepath}}/docs/sessions/#queue).\n\n{{ alta_shot \"alta-desktop-home.webp\" \"alta-home.png\" \"The workspace\" \"The main workspace.\" }}\n");
                File.WriteAllText(Path.Combine(fixture.GuideRoot, "sessions.md"), "---\ntitle: Sessions\n---\n\n# Sessions\n\nA session is a conversation.\n\n## Queue\n\nA session keeps a **prompt queue** while it runs.\n\n## More\n\n" + string.Concat(Enumerable.Repeat("One more line of the page.\n", 4)));
                File.WriteAllText(Path.Combine(fixture.GuideRoot, "orphan.md"), "# Not in the menu\n");
                File.WriteAllText(Path.Combine(fixture.GuideRoot, "plugins", "readme.md"), "---\ntitle: Plugins\n---\n\n# Plugins\n");
                File.WriteAllText(Path.Combine(fixture.GuideRoot, "plugins", "git.md"), "---\ntitle: Git\n---\n\n# Git\n\n## Sign in\n");
                File.WriteAllBytes(Path.Combine(fixture.GuideRoot, "img", "alta-desktop-home.webp"), [82, 73, 70, 70]);
            }

            var options = new CatalogOptions { GlobalRoot = Path.Combine(root, "home") };
            var services = new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options));
            if (guide) services.Add(fixture.Documentation);
            if (guide && view) services.Add<IAltaDocumentationView>(fixture.View);
            if (plugins is not null) services.Add(plugins);
            var registry = new AltaCommandRegistry();
            services.Add(registry);
            fixture.Dispatcher = new AltaCommandDispatcher(registry, services);
            return fixture;
        }

        public Task<AltaCommandResult> RunAsync(params string[] args) => Dispatcher.InvokeAsync(args, caller: Mcp).AsTask();

        public async Task<AltaCommandResult> OkAsync(params string[] args)
        {
            var result = await Dispatcher.InvokeAsync(args, caller: Mcp);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}");
            return result;
        }

        public async Task<JsonElement> FailsAsync(int exitCode, string code, params string[] args)
        {
            var result = await Dispatcher.InvokeAsync(args, caller: Mcp);
            Assert.AreEqual(exitCode, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}");
            var error = Records(result).Single(static line => Type(line) == "alta.error");
            Assert.AreEqual(code, error.GetProperty("code").GetString(), string.Join(' ', args));
            return error;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A fixture that the system still holds is left to the temporary folder.
            }
        }
    }
}
