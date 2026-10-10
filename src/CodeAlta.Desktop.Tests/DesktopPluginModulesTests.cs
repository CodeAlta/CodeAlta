using System.Text;
using CodeAlta.Desktop;
using CodeAlta.Plugins.Abstractions;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

/// <summary>What the page may import from a plugin: the files of its package folder and the modules it gives as text, and nothing else.</summary>
[TestClass]
public sealed class DesktopPluginModulesTests
{
    private const string Key = "plugin:board";

    [TestMethod]
    public async Task Publish_GivesAnAddressThatChangesWithTheFilesAndWithTheActivation()
    {
        using var package = new Package();
        package.Write("ui/board.js", "export default () => null;");
        var activation = new object();
        var owners = new List<PluginModuleOwner> { new(Key, package.Root, activation) };
        var modules = new DesktopPluginModules(() => owners);

        var first = modules.Publish(owners[0], PluginScript.File("ui/board.js"));
        Assert.IsNotNull(first);
        StringAssert.StartsWith(first, $"/plugin/{DesktopPluginModules.KeySegment(Key)}/");
        StringAssert.EndsWith(first, "/ui/board.js");
        Assert.AreEqual(first, modules.Publish(owners[0], PluginScript.File("ui/board.js")), "the same files are the same address: a browser keeps its module");

        // A changed file is a new module.
        package.Write("ui/board.js", "export default () => 1;", touch: TimeSpan.FromSeconds(5));
        var edited = modules.Publish(owners[0], PluginScript.File("ui/board.js"));
        Assert.AreNotEqual(first, edited);
        // A file nothing imports yet is part of what the package is: a new import in it changes the whole.
        package.Write("ui/helper.js", "export const x = 1;");
        Assert.AreNotEqual(edited, modules.Publish(owners[0], PluginScript.File("ui/board.js")));
        // A reload of the plugin, with the same files, is a new address too: the module it mounts is released with the old plugin.
        var reloaded = new PluginModuleOwner(Key, package.Root, new object());
        Assert.AreNotEqual(modules.Publish(owners[0], PluginScript.File("ui/board.js")), modules.Publish(reloaded, PluginScript.File("ui/board.js")));
        await Task.CompletedTask;
    }

    [TestMethod]
    public void OneRead_LooksAtThePackageFolderOnce_AndTheNextReadSeesWhatChanged()
    {
        using var package = new Package();
        package.Write("ui/board.js", "export default () => null;");
        package.Write("ui/card.js", "export default () => null;");
        var owners = new List<PluginModuleOwner> { new(Key, package.Root, new object()) };
        var modules = new DesktopPluginModules(() => owners);
        static string Stamp(string? path) => path!.Split('/')[3];

        // A read that names several scripts of a plugin (its regions, the cards of a session) gives them one version of the package.
        var read = modules.StartRead();
        var board = read.PublishFor(Key, PluginScript.File("ui/board.js"));
        package.Write("ui/helper.js", "export const x = 1;");
        var card = read.PublishFor(Key, PluginScript.File("ui/card.js"));
        Assert.AreEqual(Stamp(board), Stamp(card), "the folder was looked at once for the read");
        Assert.AreNotEqual(Stamp(board), Stamp(modules.Publish(owners[0], PluginScript.File("ui/board.js"))), "a call that is no part of the read looks at the folder itself");

        // The next read looks again: a file that changed is a new address from then on, and so is one that changes later.
        var next = modules.StartRead().PublishFor(Key, PluginScript.File("ui/board.js"));
        Assert.AreNotEqual(Stamp(board), Stamp(next));
        package.Write("ui/board.js", "export default () => 1;", touch: TimeSpan.FromSeconds(5));
        Assert.AreNotEqual(Stamp(next), Stamp(modules.StartRead().PublishFor(Key, PluginScript.File("ui/board.js"))));

        // A read refuses what a single call refuses, and another activation of the plugin is another version within the same read.
        Assert.IsNull(read.PublishFor(Key, PluginScript.File("ui/missing.js")));
        Assert.IsNull(read.PublishFor("source:not-there", PluginScript.File("ui/board.js")));
        var again = modules.StartRead();
        var before = again.PublishFor(Key, PluginScript.File("ui/board.js"));
        owners[0] = new(Key, package.Root, new object());
        Assert.AreNotEqual(Stamp(before), Stamp(again.PublishFor(Key, PluginScript.File("ui/board.js"))), "a reloaded plugin is not given the version of the one before");
    }

    [TestMethod]
    public void Publish_RefusesWhatIsNotAScriptOfThePackage()
    {
        using var package = new Package();
        package.Write("ui/board.js", "1");
        package.Write("ui/style.css", "a{}");
        package.Write("plugin.cs", "class X {}");
        var owner = new PluginModuleOwner(Key, package.Root, new object());
        var modules = new DesktopPluginModules(() => [owner]);

        Assert.IsNull(modules.Publish(owner, PluginScript.File("ui/missing.js")), "a file that is not there");
        Assert.IsNull(modules.Publish(owner, new PluginScript { Path = "plugin.cs" }), "a source file is not a script");
        Assert.IsNull(modules.Publish(owner, new PluginScript { Path = "ui/style.css" }), "a style sheet is not an entry");
        Assert.IsNull(modules.Publish(owner, new PluginScript { Path = "../escape.js" }), "outside the package folder");
        Assert.IsNull(modules.Publish(owner, new PluginScript { Path = "ui/../../escape.js" }));
        Assert.IsNull(modules.Publish(owner, new PluginScript { Path = "ui\\board.js" }), "a back slash is not a separator of an address");
        Assert.IsNull(modules.Publish(new PluginModuleOwner(Key, null, new object()), PluginScript.File("ui/board.js")), "a built-in plugin has no package folder");
        Assert.IsNull(modules.Publish(owner, new PluginScript()), "no entry at all");
        Assert.ThrowsExactly<ArgumentException>(() => PluginScript.File("../x.js"));
        Assert.ThrowsExactly<ArgumentException>(() => PluginScript.File("ui/board.cs"));
        Assert.ThrowsExactly<ArgumentException>(() => PluginScript.File("/abs/board.js"));
        Assert.ThrowsExactly<ArgumentException>(() => PluginScript.Inline(" "));
    }

    [TestMethod]
    public void GetResponse_ServesTheFilesOfThePackageWithTheirTypes()
    {
        using var package = new Package();
        package.Write("ui/board.js", "export default 1;");
        package.Write("ui/board.mjs", "export default 2;");
        package.Write("ui/board.css", "a{}");
        package.Write("data/rows.json", "[1]");
        package.Write("icons/star.svg", "<svg xmlns='http://www.w3.org/2000/svg'/>");
        package.Write("ui/kept name.js", "export default 3;");
        var owner = new PluginModuleOwner(Key, package.Root, new object());
        var modules = new DesktopPluginModules(() => [owner]);
        var entry = modules.Publish(owner, PluginScript.File("ui/board.js"))!;
        var stamp = entry.Split('/')[3];

        string Address(string path) => $"/plugin/{DesktopPluginModules.KeySegment(Key)}/{stamp}/{path}";
        foreach (var (path, type, text) in new[]
        {
            ("ui/board.js", "text/javascript; charset=utf-8", "export default 1;"), ("ui/board.mjs", "text/javascript; charset=utf-8", "export default 2;"),
            ("ui/board.css", "text/css; charset=utf-8", "a{}"), ("data/rows.json", "application/json; charset=utf-8", "[1]"),
            ("icons/star.svg", "image/svg+xml", "<svg xmlns='http://www.w3.org/2000/svg'/>"), ("ui/kept%20name.js", "text/javascript; charset=utf-8", "export default 3;"),
        })
        {
            var response = modules.GetResponse(Request(Address(path)));
            Assert.IsNotNull(response, path);
            Assert.AreEqual((200, type, text), (response.StatusCode, response.MimeType, Encoding.UTF8.GetString(response.Bytes.Span)), path);
        }

        Assert.AreEqual(200, modules.GetResponse(Request(Address("ui/board.js"), "HEAD"))!.StatusCode);
        Assert.AreEqual(405, modules.GetResponse(Request(Address("ui/board.js"), "POST"))!.StatusCode);
    }

    [TestMethod]
    public void GetResponse_NeverLeavesThePackageFolder()
    {
        using var package = new Package();
        package.Write("ui/board.js", "1");
        package.Write("../outside.js", "secret");
        package.Write("plugin.cs", "class X {}");
        package.Write("bin/Plugin.dll", "MZ");
        var owner = new PluginModuleOwner(Key, package.Root, new object());
        var modules = new DesktopPluginModules(() => [owner]);
        var key = DesktopPluginModules.KeySegment(Key);

        foreach (var attack in new[]
        {
            "../outside.js", "ui/../../outside.js", "%2e%2e/outside.js", "ui/%2e%2e/%2e%2e/outside.js", "..%2foutside.js", "ui%2f..%2f..%2foutside.js", "..%5coutside.js",
            "ui\\..\\..\\outside.js", "C:/Windows/win.ini", "C%3A/Windows/win.ini", "/outside.js", "ui//board.js", "ui/board.js::$DATA", "ui/board.js%00.css", "ui/board.js.",
            "ui/", "ui", "", "plugin.cs", "bin/Plugin.dll", "ui/board.js/../board.js/x.js",
        })
        {
            var response = modules.GetResponse(Request($"/plugin/{key}/stamp/{attack}"));
            Assert.IsTrue(response is null || response.StatusCode == 404 || response.StatusCode == 400 || response.StatusCode == 405,
                $"{attack}: {response?.StatusCode}");
            Assert.IsFalse(response is not null && response.Bytes.Length > 0 && Encoding.UTF8.GetString(response.Bytes.Span).Contains("secret", StringComparison.Ordinal), attack);
        }

        Assert.AreEqual(404, modules.GetResponse(Request($"/plugin/{key}/stamp/plugin.cs"))!.StatusCode, "no source");
        Assert.AreEqual(404, modules.GetResponse(Request($"/plugin/{key}/stamp/bin/Plugin.dll"))!.StatusCode, "no binary");
        Assert.AreEqual(200, modules.GetResponse(Request($"/plugin/{key}/stamp/ui/board.js"))!.StatusCode, "what is allowed still is");
    }

    [TestMethod]
    public void GetResponse_DoesNotFollowALinkOutOfThePackage()
    {
        using var outside = new Package();
        outside.Write("secret.js", "secret");
        using var package = new Package();
        package.Write("ui/board.js", "1");
        try { Directory.CreateSymbolicLink(Path.Combine(package.Root, "link"), outside.Root); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { Assert.Inconclusive("Links cannot be created here."); }
        try { File.CreateSymbolicLink(Path.Combine(package.Root, "ui", "alias.js"), Path.Combine(outside.Root, "secret.js")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        var owner = new PluginModuleOwner(Key, package.Root, new object());
        var modules = new DesktopPluginModules(() => [owner]);
        var key = DesktopPluginModules.KeySegment(Key);

        Assert.AreEqual(404, modules.GetResponse(Request($"/plugin/{key}/s/link/secret.js"))!.StatusCode, "a linked folder");
        if (File.Exists(Path.Combine(package.Root, "ui", "alias.js"))) Assert.AreEqual(404, modules.GetResponse(Request($"/plugin/{key}/s/ui/alias.js"))!.StatusCode, "a linked file");
        Assert.IsNull(modules.Publish(owner, new PluginScript { Path = "link/secret.js" }));
    }

    [TestMethod]
    public void GetResponse_RefusesALargeFile_AnUnknownPlugin_AndAPluginThatIsNotActive()
    {
        using var package = new Package();
        package.Write("ui/small.js", "1");
        using (var large = File.Create(Path.Combine(package.Root, "ui", "large.js"))) large.SetLength(DesktopPluginModules.MaximumFileBytes + 1);
        using (var allowed = File.Create(Path.Combine(package.Root, "ui", "edge.js"))) allowed.SetLength(DesktopPluginModules.MaximumFileBytes);
        var owners = new List<PluginModuleOwner> { new(Key, package.Root, new object()) };
        var modules = new DesktopPluginModules(() => owners);
        var key = DesktopPluginModules.KeySegment(Key);

        Assert.AreEqual(404, modules.GetResponse(Request($"/plugin/{key}/s/ui/large.js"))!.StatusCode);
        Assert.AreEqual(200, modules.GetResponse(Request($"/plugin/{key}/s/ui/edge.js"))!.StatusCode);
        Assert.IsNull(modules.Publish(owners[0], new PluginScript { Path = "ui/large.js" }));
        Assert.AreEqual(404, modules.GetResponse(Request($"/plugin/{DesktopPluginModules.KeySegment("plugin:other")}/s/ui/small.js"))!.StatusCode, "a plugin that is not active");
        Assert.AreEqual(404, modules.GetResponse(Request("/plugin/not*base64/s/ui/small.js"))!.StatusCode);
        Assert.AreEqual(404, modules.GetResponse(Request($"/plugin/{key}/s"))!.StatusCode, "no file");
        Assert.AreEqual(404, modules.GetResponse(Request("/other/x.js"))!.StatusCode);
        owners.Clear();
        Assert.AreEqual(404, modules.GetResponse(Request($"/plugin/{key}/s/ui/small.js"))!.StatusCode, "a plugin that stopped serves nothing");
    }

    [TestMethod]
    public void Inline_ModulesAreServedFromMemoryUnderAGeneratedName_AndEachVersionIsAnAddress()
    {
        var owner = new PluginModuleOwner(Key, null, new object());
        var modules = new DesktopPluginModules(() => [owner]);
        var script = PluginScript.Inline("import { a } from './helper.js'; export default () => a;").WithModule("helper.js", "export const a = 1;");

        var entry = modules.Publish(owner, script)!;
        StringAssert.EndsWith(entry, "/main.js");
        Assert.AreEqual(entry, modules.Publish(owner, script), "the same text is the same address");
        string Next(string path) => string.Join('/', entry.Split('/').SkipLast(1)) + "/" + path;
        Assert.AreEqual("import { a } from './helper.js'; export default () => a;", Encoding.UTF8.GetString(modules.GetResponse(Request(entry))!.Bytes.Span));
        Assert.AreEqual("export const a = 1;", Encoding.UTF8.GetString(modules.GetResponse(Request(Next("helper.js")))!.Bytes.Span), "an import of the entry resolves beside it");
        Assert.AreEqual(404, modules.GetResponse(Request(Next("other.js")))!.StatusCode);

        var changed = modules.Publish(owner, PluginScript.Inline("export default () => 2;"))!;
        Assert.AreNotEqual(entry, changed, "a new text is a new module");
        Assert.AreEqual(200, modules.GetResponse(Request(entry))!.StatusCode, "the old version stays for a tab that still imports it");

        // A few versions are kept for each plugin, the oldest let go.
        var first = modules.Publish(owner, PluginScript.Inline("export default 0;"))!;
        for (var index = 1; index <= DesktopPluginModules.MaximumSetsPerPlugin; index++) modules.Publish(owner, PluginScript.Inline($"export default {index};"));
        Assert.AreEqual(404, modules.GetResponse(Request(first))!.StatusCode);
        Assert.AreEqual(404, modules.GetResponse(Request(entry))!.StatusCode);

        // Nothing is served for a name that is not a web file, or that a module cannot take.
        var tricky = PluginScript.Inline("export default 1;").WithModule("main.js", "export default 'shadow';");
        var published = modules.Publish(owner, tricky)!;
        Assert.AreEqual("export default 1;", Encoding.UTF8.GetString(modules.GetResponse(Request(published))!.Bytes.Span), "the entry cannot be shadowed");
        Assert.ThrowsExactly<ArgumentException>(() => PluginScript.Inline("x").WithModule("dir/x.js", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => PluginScript.Inline("x").WithModule("x.txt", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => PluginScript.Inline(new string('x', PluginScript.MaximumSourceLength + 1)));
    }

    [TestMethod]
    public void AnApplicationModule_IsNamedByABuiltInPluginOnly_AndIsAPathOfTheApplicationsOwnFiles()
    {
        var builtIn = new PluginModuleOwner("builtin:statistics", null, new object());
        var source = new PluginModuleOwner("plugin:board", null, new object());
        var packaged = new PluginModuleOwner("builtin:odd", Path.GetTempPath(), new object());
        var modules = new DesktopPluginModules(() => [builtIn, source, packaged]);

        Assert.AreEqual("/lib/app/statistics.js", modules.Publish(builtIn, PluginScript.App("statistics")), "the file is one of the application's own, served with them");
        Assert.AreEqual("/lib/app/statistics.js", modules.PublishFor("builtin:statistics", PluginScript.App("statistics")));
        Assert.IsNull(modules.Publish(source, PluginScript.App("statistics")), "a source plugin has no module of the application's build");
        Assert.IsNull(modules.Publish(packaged, PluginScript.App("statistics")), "nor a plugin that has a package folder, whatever its key");
        Assert.IsNull(modules.PublishFor("builtin:not-there", PluginScript.App("statistics")));
        Assert.IsNull(modules.Publish(builtIn, new PluginScript { AppModule = "../x" }), "a name that is not one is not a path");
        foreach (var bad in new[] { "", " ", "Statistics", "1stats", "a/b", "a.b", "a_b", new string('a', 65), "../x" })
            Assert.ThrowsExactly<ArgumentException>(() => PluginScript.App(bad), bad);
        Assert.IsTrue(PluginScript.App("a-1").HasEntry);

        // The server of plugin files never answers it: the application's files do.
        Assert.AreEqual(404, modules.GetResponse(Request("/plugin/" + DesktopPluginModules.KeySegment("builtin:statistics") + "/s/lib/app/statistics.js"))!.StatusCode);
    }

    [TestMethod]
    public void Prune_ForgetsWhatAPluginThatIsGoneKept()
    {
        var owners = new List<PluginModuleOwner> { new(Key, null, new object()) };
        var modules = new DesktopPluginModules(() => owners);
        var entry = modules.Publish(owners[0], PluginScript.Inline("export default 1;"))!;
        owners.Clear();
        modules.Prune();
        owners.Add(new PluginModuleOwner(Key, null, new object()));
        Assert.AreEqual(404, modules.GetResponse(Request(entry))!.StatusCode);
    }

    [TestMethod]
    public void TheResourcesOfTheApplication_AnswerThePluginPrefixOnlyThroughTheServer()
    {
        var owner = new PluginModuleOwner(Key, null, new object());
        var modules = new DesktopPluginModules(() => [owner]);
        var entry = modules.Publish(owner, PluginScript.Inline("export default 1;"))!;
        var assets = new Assets();
        var resources = new DesktopStartupResources(assets, new DesktopStartupStatus(), modules);

        Assert.AreEqual("export default 1;", Encoding.UTF8.GetString(resources.GetResponse(Request(entry))!.Bytes.Span));
        Assert.AreEqual(0, assets.Requests, "a plugin address never reaches the files of the application");
        Assert.AreEqual(404, new DesktopStartupResources(assets, new DesktopStartupStatus()).GetResponse(Request(entry))!.StatusCode, "a window without plugins serves nothing there");
        Assert.AreEqual(0, assets.Requests);
        Assert.AreEqual("index", Encoding.UTF8.GetString(resources.GetResponse(Request("/index.html"))!.Bytes.Span), "the application is served as before");
        Assert.AreEqual(1, assets.Requests);
    }

    private static NeoResourceRequest Request(string path, string method = "GET")
        => new(new Uri("app://codealta" + path), method, new Dictionary<string, string>(), null, NeoResourceKind.Script, false, default);

    private sealed class Assets : INeoResourceProvider
    {
        public int Requests { get; private set; }

        public NeoResourceResponse? GetResponse(NeoResourceRequest request)
        {
            Requests++;
            return NeoResourceResponse.FromBytes(Encoding.UTF8.GetBytes("index"), "text/html");
        }
    }

    private sealed class Package : IDisposable
    {
        private readonly string _parent = Path.Combine(Path.GetTempPath(), "CodeAlta-modules-" + Guid.NewGuid().ToString("N"));

        public Package()
        {
            Root = Path.Combine(_parent, "package");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string path, string text, TimeSpan? touch = null)
        {
            var full = Path.GetFullPath(Path.Combine(Root, path));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            if (touch is { } shift) File.SetLastWriteTimeUtc(full, DateTime.UtcNow + shift);
        }

        public void Dispose()
        {
            try { Directory.Delete(_parent, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
