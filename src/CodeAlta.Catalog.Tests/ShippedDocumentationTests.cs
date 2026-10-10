using CodeAlta.Catalog.Documentation;

namespace CodeAlta.Catalog.Tests;

/// <summary>The user guide that ships with an application: its menu, its pages and its pictures, read from one folder only.</summary>
[TestClass]
public sealed class ShippedDocumentationTests
{
    private const string Menu = """
        doc:
          - {path: readme.md, title: "<i class='bi bi-book' aria-hidden='true'></i> User Guide"}
          - {path: getting-started.md, title: "<i class='bi bi-rocket-takeoff' aria-hidden='true'></i> Getting &amp; Started"}
          - {path: plugins/readme.md, title: "<i class='bi bi-puzzle' aria-hidden='true'></i> Plugins", folder: true}
          - {path: missing.md, title: "Not shipped"}
          - {path: ../outside.md, title: "Outside"}
          - {url: "https://example.com", title: "Elsewhere"}
        """;

    private const string PluginsMenu = """
        doc:
          - {path: readme.md, title: "<i class='bi bi-puzzle' aria-hidden='true'></i> Overview"}
          - {path: git.md, title: "Git"}
          - {path: mcp.md, title: "MCP"}
        """;

    [TestMethod]
    public void Menu_IsReadFromMenuYml_WithTheMenuOfAFolderThatHasItsOwn()
    {
        using var guide = Guide.Create();
        var documentation = new ShippedDocumentation(guide.Root);
        Assert.IsTrue(documentation.Available);
        Assert.AreEqual("readme.md", documentation.Home);

        var menu = documentation.GetMenu();
        // An entry without a shipped page, one that leaves the guide and an address of the web are no entries.
        CollectionAssert.AreEqual(new[] { "readme.md", "getting-started.md", "plugins/readme.md" }, menu.Select(static item => item.Path).ToArray());
        // The title of the menu is HTML: its words are the title, and the class of its icon names the icon.
        Assert.AreEqual("User Guide", menu[0].Title);
        Assert.AreEqual("book", menu[0].Icon);
        Assert.AreEqual("Getting & Started", menu[1].Title);
        Assert.AreEqual("rocket-takeoff", menu[1].Icon);
        Assert.AreEqual(0, menu[0].Children.Count);
        // The menu of the folder, without the entry that is the page of the folder itself.
        CollectionAssert.AreEqual(new[] { "plugins/git.md", "plugins/mcp.md" }, menu[2].Children.Select(static item => item.Path).ToArray());
        Assert.AreEqual("Git", menu[2].Children[0].Title);
        Assert.IsNull(menu[2].Children[0].Icon);

        // Every page is listed with its title, whether the menu names it or not: the front matter, then the first heading, then the name.
        var pages = documentation.ListPages().ToDictionary(static page => page.Path, static page => page.Title);
        Assert.AreEqual("User Guide", pages["readme.md"]);
        Assert.AreEqual("Heading of the page", pages["orphan.md"]);
        Assert.AreEqual("mcp", pages["plugins/mcp.md"]);
        Assert.IsFalse(pages.ContainsKey("img/readme.md"));
    }

    [TestMethod]
    public void Menu_FallsBackToThePages_WhenThereIsNoMenuOrItCannotBeRead()
    {
        foreach (var menu in new string?[] { null, "doc: [unclosed", "- just\n- a list", "doc:\n  - {path: nowhere.md, title: Nothing}" })
        {
            using var guide = Guide.Create(menu: menu);
            var documentation = new ShippedDocumentation(guide.Root);
            var items = documentation.GetMenu();
            Assert.AreEqual("readme.md", items[0].Path, menu);
            Assert.AreEqual("User Guide", items[0].Title, menu);
            CollectionAssert.Contains(items.Select(static item => item.Path).ToArray(), "plugins/git.md", menu);
            // The pages of the guide come before the ones of its folders.
            Assert.IsTrue(items.Select(static item => item.Path).ToList().IndexOf("orphan.md") < items.Select(static item => item.Path).ToList().IndexOf("plugins/git.md"), menu);
        }
    }

    [TestMethod]
    public void NoGuide_IsNotAvailable_AndAnswersNothing()
    {
        using var guide = Guide.Create();
        var documentation = new ShippedDocumentation(Path.Combine(guide.Root, "nowhere"));
        Assert.IsFalse(documentation.Available);
        Assert.IsNull(documentation.Home);
        Assert.AreEqual(0, documentation.GetMenu().Count);
        Assert.IsNull(documentation.ReadPage("readme.md"));
        Assert.IsNull(documentation.ReadImage("alta-desktop-home.webp"));
        Assert.AreEqual(0, documentation.Search("guide").Count);
        Assert.ThrowsExactly<ArgumentException>(() => new ShippedDocumentation(" "));
    }

    [TestMethod]
    public void Page_IsNamedByItsPathBelowTheGuide_AndNothingElseIsRead()
    {
        using var guide = Guide.Create();
        File.WriteAllText(Path.Combine(guide.Parent, "outside.md"), "# Outside\n\nsecret");
        var documentation = new ShippedDocumentation(guide.Root);

        Assert.AreEqual("plugins/git.md", documentation.ResolvePage("plugins/git.md"));
        Assert.AreEqual("plugins/git.md", documentation.ResolvePage("Plugins/GIT.md"));
        Assert.AreEqual(Path.Combine(guide.Root, "plugins", "git.md"), documentation.GetPageFile("plugins/git.md"));
        Assert.AreEqual("plugins/git.md", documentation.FindPage(Path.Combine(guide.Root, "plugins", "git.md")));
        Assert.IsNull(documentation.FindPage(Path.Combine(guide.Parent, "outside.md")));
        Assert.IsNull(documentation.FindPage("plugins/git.md"));

        var outside = Path.Combine(guide.Parent, "outside.md");
        foreach (var path in new string?[]
        {
            null, "", " ", "../outside.md", "plugins/../../outside.md", "./readme.md", "plugins//git.md", "/readme.md", "readme.md/", "plugins\\git.md",
            outside, outside.Replace('\\', '/'), "file:///" + outside.Replace('\\', '/'), "file:readme.md", "https://example.com/readme.md", "//server/share/readme.md",
            "%2e%2e/outside.md", "..%2foutside.md", "readme.md\0", "readme.md ", "readme.md.", "C:readme.md", "readme.md:stream", "~/readme.md", "menu.yml", "img/alta-desktop-home.webp",
            new string('a', ShippedDocumentation.MaximumPathLength) + ".md",
        })
        {
            Assert.IsNull(documentation.ResolvePage(path), path);
            Assert.IsNull(documentation.GetPageFile(path), path);
            Assert.IsNull(documentation.ReadPage(path), path);
        }

        // A file that is no page, and a page that grew past the limit after it was listed.
        Assert.IsNull(documentation.ReadPage("notes.txt"));
        File.WriteAllText(Path.Combine(guide.Root, "orphan.md"), new string('x', ShippedDocumentation.MaximumPageBytes + 1));
        Assert.IsNull(documentation.ReadPage("orphan.md"));
    }

    [TestMethod]
    public void Image_IsNamedByItsFileName_AndOnlyAPictureOfTheGuideIsRead()
    {
        using var guide = Guide.Create();
        File.WriteAllBytes(Path.Combine(guide.Parent, "secret.png"), [1, 2, 3]);
        var documentation = new ShippedDocumentation(guide.Root);

        var image = documentation.ReadImage("alta-desktop-home.webp");
        Assert.IsNotNull(image);
        Assert.AreEqual("image/webp", image.MediaType);
        CollectionAssert.AreEqual(new byte[] { 82, 73, 70, 70 }, image.Content.ToArray());
        Assert.AreEqual("image/png", documentation.ReadImage("shot.png")!.MediaType);

        foreach (var name in new string?[] { null, "", "../secret.png", "img/alta-desktop-home.webp", "..\\secret.png", Path.Combine(guide.Parent, "secret.png"), "notes.svg", "readme.md", "alta-desktop-home.webp/", "file:secret.png" })
        {
            Assert.IsNull(documentation.ReadImage(name), name);
        }
    }

    [TestMethod]
    public void Links_OfTheFileSystem_AreNeverFollowed()
    {
        using var guide = Guide.Create();
        var outside = Directory.CreateDirectory(Path.Combine(guide.Parent, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(outside, "linked.md"), "# Linked\n\nsecret");
        File.WriteAllBytes(Path.Combine(outside, "linked.png"), [9]);
        try
        {
            File.CreateSymbolicLink(Path.Combine(guide.Root, "linked.md"), Path.Combine(outside, "linked.md"));
            File.CreateSymbolicLink(Path.Combine(guide.Root, "img", "linked.png"), Path.Combine(outside, "linked.png"));
            Directory.CreateSymbolicLink(Path.Combine(guide.Root, "linked"), outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("This account cannot create symbolic links.");
        }

        var documentation = new ShippedDocumentation(guide.Root);
        Assert.IsTrue(documentation.Available);
        Assert.IsNull(documentation.ReadPage("linked.md"));
        Assert.IsNull(documentation.ReadPage("linked/linked.md"));
        Assert.IsNull(documentation.ReadImage("linked.png"));
        Assert.IsFalse(documentation.ListPages().Any(static page => page.Path.Contains("linked", StringComparison.Ordinal)));

        // A page that becomes a link after it was listed is not read either.
        File.Delete(Path.Combine(guide.Root, "orphan.md"));
        File.CreateSymbolicLink(Path.Combine(guide.Root, "orphan.md"), Path.Combine(outside, "linked.md"));
        Assert.IsNull(documentation.ReadPage("orphan.md"));
    }

    [TestMethod]
    public void Page_ReplacesTheTemplatesOfTheSite_AndNamesEveryLinkedPageFromTheGuide()
    {
        using var guide = Guide.Create();
        File.WriteAllText(Path.Combine(guide.Root, "plugins", "mcp.md"), string.Join('\n',
            "---",
            "title: \"MCP servers\"",
            "---",
            "",
            "# MCP",
            "",
            "See [Git](git.md), [the guide](../readme.md#start-here), [here](#policy), [the site page]({{site.basepath}}/docs/getting-started/#install),",
            "[plugins]({{site.basepath}}/docs/plugins/), [the docs]({{site.basepath}}/docs/), [a release]({{site.basepath}}/news/1.6/), [the web](https://example.com/a.md),",
            "[outside](../../outside.md), [a file](src/Program.cs) and `[code](git.md)`.",
            "",
            "<a href=\"{{site.basepath}}/docs/plugins/git/\">Git again</a> {{ alta_yes }} {{ alta_part }} {{alta_no}} {{ unknown }}",
            "",
            "```md",
            "[kept](git.md) {{ alta_yes }}",
            "```",
            "",
            "{{ alta_shot \"alta-desktop-home.webp\" \"alta-home.png\" \"The <b>workspace</b>\" \"The main <code>workspace</code>, see [Git](git.md).\" }}",
            "",
            "{{ alta_shot \"alta-desktop-missing.webp\" \"alta-home.png\" \"Not shipped\" \"Left out.\" }}",
            "",
            "<div class=\"row g-3 my-4\">",
            "  <div class=\"col-md-6\">",
            "    <figure class=\"alta-figure mb-0\">",
            "      <img src=\"{{site.basepath}}/img/shot.png\" alt=\"Two sessions\" loading=\"lazy\">",
            "      <figcaption class=\"small text-secondary mt-2\">Two sessions,",
            "        side by side.</figcaption>",
            "    </figure>",
            "  </div>",
            "  <div class=\"col-md-6\">",
            "    <figure>",
            "      <a href=\"{{site.basepath}}/img/alta-theme.png\"><img src=\"{{site.basepath}}/img/alta-theme.png\" alt=\"Of the other application\"></a>",
            "      <figcaption>Left out with its picture.</figcaption>",
            "    </figure>",
            "  </div>",
            "</div>",
            "",
            "## Policy",
            "",
            "<figure class=\"my-4\">",
            "  <svg viewBox=\"0 0 10 10\" xmlns=\"http://www.w3.org/2000/svg\"><title id=\"t\">Prompt flow</title><style>.a { fill: red; }</style><rect class=\"a\" width=\"10\" height=\"10\" /></svg>",
            "  <figcaption>How a prompt is composed.</figcaption>",
            "</figure>",
            "",
            "<style>",
            ".site-only { color: red; }",
            "</style>",
            "",
            "Last words."));
        var documentation = new ShippedDocumentation(guide.Root);
        Assert.AreEqual("MCP servers", documentation.ListPages().Single(static page => page.Path == "plugins/mcp.md").Title);

        var page = documentation.ReadPage("plugins/mcp.md");
        Assert.IsNotNull(page);
        Assert.AreEqual("plugins/mcp.md", page.Path);
        Assert.AreEqual("MCP servers", page.Title);
        Assert.AreEqual(6, page.Blocks.Count);

        var first = page.Blocks[0].Markdown!;
        // The front matter is no text of the page.
        Assert.IsTrue(first.StartsWith("# MCP\n", StringComparison.Ordinal), first);
        StringAssert.Contains(first, "[Git](plugins/git.md)");
        StringAssert.Contains(first, "[the guide](readme.md#start-here)");
        StringAssert.Contains(first, "[here](plugins/mcp.md#policy)");
        StringAssert.Contains(first, "[the site page](getting-started.md#install)");
        StringAssert.Contains(first, "[plugins](plugins/readme.md)");
        StringAssert.Contains(first, "[the docs](readme.md)");
        // What is no shipped page stays what it was: an address of the site, of the web, a path that leaves the guide, a file.
        StringAssert.Contains(first, "[a release](https://codealta.github.io/news/1.6/)");
        StringAssert.Contains(first, "[the web](https://example.com/a.md)");
        StringAssert.Contains(first, "[outside](../../outside.md)");
        StringAssert.Contains(first, "[a file](src/Program.cs)");
        // Code is not rewritten; the templates are replaced everywhere, as the site does.
        StringAssert.Contains(first, "`[code](git.md)`");
        StringAssert.Contains(first, "<a href=\"plugins/git.md\">Git again</a> ✓ ◐ – {{ unknown }}");
        StringAssert.Contains(first, "```md\n[kept](git.md) ✓\n```");

        // A screenshot is the picture of this application, with what it shows and its caption.
        Assert.AreEqual(new ShippedDocumentationFigure("alta-desktop-home.webp", null, "The workspace", "The main <code>workspace</code>, see [Git](plugins/git.md)."), page.Blocks[1].Figure);
        // The picture of a figure, without the layout around it; a picture that is not shipped is left out with its caption.
        Assert.AreEqual(new ShippedDocumentationFigure("shot.png", null, "Two sessions", "Two sessions, side by side."), page.Blocks[2].Figure);
        Assert.AreEqual("## Policy", page.Blocks[3].Markdown);
        var drawing = page.Blocks[4].Figure!;
        Assert.IsNull(drawing.Image);
        Assert.IsTrue(drawing.Svg!.StartsWith("<svg ", StringComparison.Ordinal) && drawing.Svg.EndsWith("</svg>", StringComparison.Ordinal));
        StringAssert.Contains(drawing.Svg, "<style>.a { fill: red; }</style>");
        Assert.AreEqual("Prompt flow", drawing.Alt);
        Assert.AreEqual("How a prompt is composed.", drawing.Caption);
        // The styles of the site are left out.
        Assert.AreEqual("Last words.", page.Blocks[5].Markdown);
        Assert.IsFalse(page.Blocks.Any(static block => block.Markdown?.Contains("site-only", StringComparison.Ordinal) == true));
        Assert.IsFalse(page.Blocks.Any(static block => block.Markdown?.Contains("<div", StringComparison.Ordinal) == true));

        var markdown = page.ToMarkdown();
        StringAssert.Contains(markdown, "![The workspace](img/alta-desktop-home.webp)\n\nThe main <code>workspace</code>, see [Git](plugins/git.md).\n");
        StringAssert.Contains(markdown, "*Prompt flow*\n\nHow a prompt is composed.\n");
        Assert.IsFalse(markdown.Contains("<svg", StringComparison.Ordinal));
        Assert.IsTrue(markdown.EndsWith("Last words.\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Page_KeepsWhatOnlyLooksLikeAFigure()
    {
        using var guide = Guide.Create();
        File.WriteAllText(Path.Combine(guide.Root, "orphan.md"), string.Join('\n',
            "# Markup",
            "",
            "A plugin writes `<figure>` and `</figure>` around a picture, or <figure> in a sentence.",
            "",
            "Kept text.",
            "",
            "<figure>",
            "  <img src=\"{{site.basepath}}/img/shot.png\" alt=\"A shot\">",
            "</figure>",
            "",
            "<figure>",
            "A figure that is never closed stays text.",
            "",
            "<style>",
            ".never-closed { color: red; }"));
        var page = new ShippedDocumentation(guide.Root).ReadPage("orphan.md")!;
        Assert.AreEqual(3, page.Blocks.Count);
        StringAssert.Contains(page.Blocks[0].Markdown, "or <figure> in a sentence.\n\nKept text.");
        Assert.AreEqual("shot.png", page.Blocks[1].Figure!.Image);
        Assert.AreEqual("<figure>\nA figure that is never closed stays text.\n\n<style>\n.never-closed { color: red; }", page.Blocks[2].Markdown);
    }

    [TestMethod]
    public void Page_LeavesOutTheClassesTheSiteGivesABlock_AndKeepsTheBlock()
    {
        using var guide = Guide.Create();
        File.WriteAllText(Path.Combine(guide.Root, "orphan.md"), string.Join('\n',
            "# Worktrees",
            "",
            "{.table}",
            "| You see | What it means |",
            "| --- | --- |",
            "| A branch | `{.table}` is how the site styles it |",
            "",
            "Text before a table.",
            "   {.table .table-sm}  ",
            "| Choice | Folder |",
            "| --- | --- |",
            "",
            "{.lead}",
            "A paragraph the site styles.",
            "",
            "A sentence that ends with {.table}",
            "and `{.table}` in a sentence stay.",
            "",
            "```md",
            "{.table}",
            "| An example | of the syntax |",
            "```",
            "",
            "    {.table}",
            "    | indented code |",
            "",
            "{#custom-id}",
            "## A heading with an address of its own",
            "",
            "{.table key=value}",
            "| Not only classes |",
            "",
            "{.table}",
            "",
            "{.table}"));
        var documentation = new ShippedDocumentation(guide.Root);
        var markdown = documentation.ReadPage("orphan.md")!.Blocks.Single().Markdown!;
        Assert.AreEqual(string.Join('\n',
            "# Worktrees",
            "",
            // The line of classes is gone, and the table under it is whole, with what it quotes.
            "| You see | What it means |",
            "| --- | --- |",
            "| A branch | `{.table}` is how the site styles it |",
            "",
            "Text before a table.",
            "| Choice | Folder |",
            "| --- | --- |",
            "",
            "A paragraph the site styles.",
            "",
            "A sentence that ends with {.table}",
            "and `{.table}` in a sentence stay.",
            "",
            // Code is what it is written as.
            "```md",
            "{.table}",
            "| An example | of the syntax |",
            "```",
            "",
            "    {.table}",
            "    | indented code |",
            "",
            // Only classes are left out: anything else the line says stays to be seen.
            "{#custom-id}",
            "## A heading with an address of its own",
            "",
            "{.table key=value}",
            "| Not only classes |",
            "",
            // A line of classes with no block under it styles nothing: it stays.
            "{.table}",
            "",
            "{.table}"), markdown);
        // The search and the Markdown an agent reads are the same text.
        Assert.AreEqual(0, documentation.Search("{.lead}").Count);
        Assert.IsFalse(documentation.ReadPage("orphan.md")!.ToMarkdown().Contains("{.table .table-sm}", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Search_FindsATextInThePages_UnderItsHeading()
    {
        using var guide = Guide.Create();
        File.WriteAllText(Path.Combine(guide.Root, "getting-started.md"), "---\ntitle: Getting Started\n---\n\n# Getting Started\n\nInstall the tools.\n\n## Launch\n\n- Run **`alta`** to start the [desktop app](readme.md).\n");
        var documentation = new ShippedDocumentation(guide.Root);

        var hits = documentation.Search("  DESKTOP APP ");
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(new ShippedDocumentationSearchHit("getting-started.md", "Getting Started", "Launch", "Run alta to start the desktop app."), hits[0]);
        // A heading is a place too, under itself.
        Assert.AreEqual("Launch", documentation.Search("launch").Single().Heading);
        Assert.AreEqual(0, documentation.Search("a").Count);
        Assert.AreEqual(0, documentation.Search(null).Count);
        Assert.AreEqual(0, documentation.Search("nowhere in the guide").Count);
        // The pages of the menu come first.
        Assert.AreEqual("readme.md", documentation.Search("guide")[0].Path);
    }

    [TestMethod]
    public void Picture_SaysItsSize_FromTheStartOfItsFile()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0x05, 0x00, 0, 0, 0x03, 0x20];
        Assert.AreEqual((1280, 800), ShippedDocumentation.ImageSize(png));
        Assert.AreEqual((320, 200), ShippedDocumentation.ImageSize([.. "GIF89a"u8, 0x40, 0x01, 0xC8, 0x00]));
        // The three forms of WebP: extended, lossless, lossy.
        byte[] riff = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8];
        Assert.AreEqual((1280, 800), ShippedDocumentation.ImageSize([.. riff, .. "VP8X"u8, 10, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0x04, 0x00, 0x1F, 0x03, 0x00]));
        // 14 bits of width - 1, then 14 bits of height - 1: 1279 and 799.
        Assert.AreEqual((1280, 800), ShippedDocumentation.ImageSize([.. riff, .. "VP8L"u8, 0, 0, 0, 0, 0x2F, 0xFF, 0xC4, 0xC7, 0x00, 0, 0, 0, 0, 0]));
        Assert.AreEqual((1280, 800), ShippedDocumentation.ImageSize([.. riff, .. "VP8 "u8, 0, 0, 0, 0, 0, 0, 0, 0x9D, 0x01, 0x2A, 0x00, 0x05, 0x20, 0x03]));
        // What is no picture this reads, and a picture that is cut short, say nothing.
        Assert.IsNull(ShippedDocumentation.ImageSize([82, 73, 70, 70]));
        Assert.IsNull(ShippedDocumentation.ImageSize(png.AsSpan(0, 20)));
        Assert.IsNull(ShippedDocumentation.ImageSize([.. riff, .. "VP8X"u8, 10, 0, 0, 0]));
        Assert.IsNull(ShippedDocumentation.ImageSize([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));

        // A page gives each of its pictures the size its file declares.
        using var guide = Guide.Create();
        File.WriteAllBytes(Path.Combine(guide.Root, "img", "shot.png"), png);
        File.WriteAllText(Path.Combine(guide.Root, "orphan.md"), "{{ alta_shot \"shot.png\" \"x.png\" \"A shot\" \"\" }}\n\n{{ alta_shot \"alta-desktop-home.webp\" \"x.png\" \"No size\" \"\" }}\n");
        var page = new ShippedDocumentation(guide.Root).ReadPage("orphan.md")!;
        Assert.AreEqual(new ShippedDocumentationFigure("shot.png", null, "A shot", null) { Width = 1280, Height = 800 }, page.Blocks[0].Figure);
        Assert.AreEqual(new ShippedDocumentationFigure("alta-desktop-home.webp", null, "No size", null), page.Blocks[1].Figure);
    }

    [TestMethod]
    public void FrontMatter_AndFirstHeading_GiveTheTitle()
    {
        Assert.AreEqual(("User Guide", "\n# Body\n"), ShippedDocumentationMarkup.SplitFrontMatter("---\r\ntitle: User Guide\r\nother: 1\r\n---\r\n\r\n# Body\r\n"));
        Assert.AreEqual(("It's", "x"), ShippedDocumentationMarkup.SplitFrontMatter("---\ntitle: 'It's'\n---\nx"));
        Assert.AreEqual((null, "---\ntitle: never closed\n"), ShippedDocumentationMarkup.SplitFrontMatter("---\ntitle: never closed\n"));
        Assert.AreEqual((null, "# No front matter\n"), ShippedDocumentationMarkup.SplitFrontMatter("# No front matter\n"));
        Assert.AreEqual((null, ""), ShippedDocumentationMarkup.SplitFrontMatter("---\n---"));
        Assert.AreEqual("Real", ShippedDocumentationMarkup.FirstHeading("```\n# In code\n```\n\n## Real ##\n"));
        Assert.IsNull(ShippedDocumentationMarkup.FirstHeading("No heading\n#NotOne\n"));
    }

    private sealed class Guide : IDisposable
    {
        private Guide(string parent)
        {
            Parent = parent;
            Root = Path.Combine(parent, "user-guide");
        }

        public string Parent { get; }

        public string Root { get; }

        public static Guide Create(string? menu = Menu)
        {
            var guide = new Guide(Path.Combine(Path.GetTempPath(), $"CodeAlta.Documentation.Tests.{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Path.Combine(guide.Root, "plugins"));
            Directory.CreateDirectory(Path.Combine(guide.Root, "img"));
            if (menu is not null) File.WriteAllText(Path.Combine(guide.Root, "menu.yml"), menu);
            File.WriteAllText(Path.Combine(guide.Root, "plugins", "menu.yml"), PluginsMenu);
            File.WriteAllText(Path.Combine(guide.Root, "readme.md"), "---\ntitle: User Guide\n---\n\n# User Guide\n\nThe guide of the application.\n\n## Start here\n\n1. [Getting Started](getting-started.md)\n");
            File.WriteAllText(Path.Combine(guide.Root, "getting-started.md"), "---\ntitle: Getting Started\n---\n\n# Getting Started\n\nInstall the tools.\n");
            File.WriteAllText(Path.Combine(guide.Root, "orphan.md"), "Some text first.\n\n## Heading of the page\n");
            File.WriteAllText(Path.Combine(guide.Root, "notes.txt"), "No page.");
            File.WriteAllText(Path.Combine(guide.Root, "plugins", "readme.md"), "---\ntitle: Plugins\n---\n\n# Plugins\n");
            File.WriteAllText(Path.Combine(guide.Root, "plugins", "git.md"), "---\ntitle: Git\n---\n\n# Git\n");
            File.WriteAllText(Path.Combine(guide.Root, "plugins", "mcp.md"), "No title here.\n");
            // A page among the pictures is no page of the guide.
            File.WriteAllText(Path.Combine(guide.Root, "img", "readme.md"), "# Pictures\n");
            File.WriteAllBytes(Path.Combine(guide.Root, "img", "alta-desktop-home.webp"), [82, 73, 70, 70]);
            File.WriteAllBytes(Path.Combine(guide.Root, "img", "shot.png"), [137, 80, 78, 71]);
            File.WriteAllText(Path.Combine(guide.Root, "img", "notes.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
            return guide;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Parent)) Directory.Delete(Parent, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A fixture that the system still holds is left to the temporary folder.
            }
        }
    }
}
