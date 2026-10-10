using System.Text.RegularExpressions;
using CodeAlta.Catalog.Documentation;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Desktop.Tests;

/// <summary>The user guide of the site ships beside the desktop application, where its agents read it and its window shows it.</summary>
[TestClass]
public sealed partial class ShippedUserGuideTests
{
    private static string GuideRoot => new FileSystemPromptContentLocator().GetRoots(new SystemPromptDiscoveryContext()).ShippedUserGuideRoot;

    [TestMethod]
    public void TheUserGuide_IsBesideTheApplication_WithThePicturesOfTheDesktopItsPagesShow()
    {
        var guide = GuideRoot;
        Assert.AreEqual(Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "content", "user-guide"), guide);
        foreach (var page in new[] { "readme.md", "getting-started.md", "workspace.md", "sessions.md", "issues.md", Path.Combine("plugins", "readme.md") })
        {
            Assert.IsTrue(File.Exists(Path.Combine(guide, page)), page);
        }

        // Every picture of the desktop application a page names is there: an agent that is asked where something
        // is can look at it.
        var pictures = Directory.GetFiles(guide, "*.md", SearchOption.AllDirectories)
            .SelectMany(static page => DesktopPicture().Matches(File.ReadAllText(page)).Select(static match => match.Value)).Distinct(StringComparer.Ordinal).ToArray();
        Assert.IsTrue(pictures.Length > 20, $"{pictures.Length} pictures are named.");
        foreach (var picture in pictures)
        {
            Assert.IsTrue(File.Exists(Path.Combine(guide, "img", picture)), picture);
        }

        // Nothing else of the site is shipped: no page of the site itself, no picture of the other application.
        Assert.IsTrue(Directory.GetFiles(Path.Combine(guide, "img")).All(static file => Path.GetFileName(file).StartsWith("alta-desktop-", StringComparison.Ordinal)));
        // Besides the pages, only their navigation: the menu of the guide and the one of each folder that has its own.
        var others = Directory.GetFiles(guide, "*", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !file.StartsWith(Path.Combine(guide, "img") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(guide, file).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "menu.yml", "plugins/menu.yml" }, others);
    }

    [TestMethod]
    public void TheNavigation_IsTheMenuOfTheSite_AndNamesOnlyPagesThatShip()
    {
        var documentation = new ShippedDocumentation(GuideRoot);
        Assert.IsTrue(documentation.Available);
        Assert.AreEqual("readme.md", documentation.Home);

        var menu = documentation.GetMenu();
        // The menu of the site, entry for entry: a page that is added to it is in the window at the next build.
        var written = MenuEntry().Matches(File.ReadAllText(Path.Combine(GuideRoot, "menu.yml"))).Select(static match => match.Groups[1].Value).ToArray();
        Assert.IsTrue(written.Length >= 15, $"{written.Length} entries are written.");
        CollectionAssert.AreEqual(written, menu.Select(static item => item.Path).ToArray());
        Assert.AreEqual("User Guide", menu[0].Title);
        Assert.AreEqual("book", menu[0].Icon);
        Assert.IsTrue(menu.All(static item => item.Title.Length > 0 && !item.Title.Contains('<') && item.Icon is { Length: > 0 }));

        // The folder of the plugins has its own menu, whose first entry is the page of the folder.
        var plugins = menu.Single(static item => item.Path == "plugins/readme.md");
        var pluginsWritten = MenuEntry().Matches(File.ReadAllText(Path.Combine(GuideRoot, "plugins", "menu.yml"))).Select(static match => "plugins/" + match.Groups[1].Value).ToArray();
        CollectionAssert.AreEqual(pluginsWritten.Where(static path => path != "plugins/readme.md").ToArray(), plugins.Children.Select(static item => item.Path).ToArray());
        Assert.IsTrue(plugins.Children.Count >= 4);
        Assert.IsTrue(menu.Where(static item => item.Path != "plugins/readme.md").All(static item => item.Children.Count == 0));

        // Every page that ships is in the navigation: none can only be reached by a link.
        var named = menu.SelectMany(static item => item.Children.Prepend(item)).Select(static item => item.Path).ToHashSet(StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(documentation.ListPages().Select(static page => page.Path).ToArray(), named.ToArray());
    }

    [TestMethod]
    public void EveryPage_IsReadAsPlainMarkdown_WithLinksAndPicturesThatShip()
    {
        var documentation = new ShippedDocumentation(GuideRoot);
        var pages = documentation.ListPages();
        Assert.IsTrue(pages.Count >= 20, $"{pages.Count} pages ship.");
        int links = 0, figures = 0, drawings = 0, tableRows = 0;
        foreach (var info in pages)
        {
            var page = documentation.ReadPage(info.Path);
            Assert.IsNotNull(page, info.Path);
            Assert.IsTrue(page.Title.Length > 0 && page.Blocks.Count > 0, info.Path);
            foreach (var block in page.Blocks)
            {
                if (block.Figure is { } figure)
                {
                    // A picture is one that ships, or a drawing of the page; what it shows is said.
                    if (figure.Image is { } image)
                    {
                        figures++;
                        Assert.IsNotNull(documentation.ReadImage(image), $"{info.Path}: {image}");
                        Assert.IsTrue(figure.Alt.Length > 0, $"{info.Path}: {image}");
                        // Its size is known before its file is read: the window keeps the place of the picture.
                        Assert.IsTrue(figure.Width is >= 200 and <= 8000 && figure.Height is >= 100 and <= 8000, $"{info.Path}: {image} is {figure.Width}x{figure.Height}");
                    }
                    else
                    {
                        drawings++;
                        StringAssert.StartsWith(figure.Svg, "<svg", info.Path);
                    }

                    Assert.IsFalse(figure.Caption?.Contains("{{", StringComparison.Ordinal) == true, info.Path);
                    continue;
                }

                var markdown = block.Markdown!;
                // No template of the site is left, and nothing that only the site lays out.
                Assert.IsFalse(markdown.Contains("{{", StringComparison.Ordinal), $"{info.Path}: {markdown[..Math.Min(markdown.Length, 200)]}");
                Assert.IsFalse(markdown.Contains("<figure", StringComparison.OrdinalIgnoreCase) || markdown.Contains("<style", StringComparison.OrdinalIgnoreCase)
                    || markdown.Contains("<img", StringComparison.OrdinalIgnoreCase), info.Path);
                // Nor a line that only gives the next block its classes or its name on the site (`{.table}` above a table): outside code, it is no text of the page.
                var fenced = false;
                foreach (var line in markdown.Split('\n'))
                {
                    if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal)) { fenced = !fenced; continue; }
                    if (fenced) continue;
                    Assert.IsFalse(SiteAttributes().IsMatch(line), $"{info.Path}: {line}");
                    if (line.StartsWith('|')) tableRows++;
                }

                // A link to a page names it from the folder of the guide, and the page ships.
                foreach (Match link in PageLink().Matches(markdown))
                {
                    links++;
                    Assert.AreEqual(link.Groups[1].Value, documentation.ResolvePage(link.Groups[1].Value), $"{info.Path}: {link.Value}");
                }
            }

            StringAssert.Contains(page.ToMarkdown(), page.Blocks.First(static block => block.Markdown is not null).Markdown, info.Path);
        }

        Assert.IsTrue(links > 100, $"{links} links between pages.");
        Assert.IsTrue(figures > 60, $"{figures} pictures.");
        Assert.AreEqual(1, drawings);
        // The tables the site styles are all there: the files write a line of classes above each, and every row of every table is read.
        var files = Directory.GetFiles(GuideRoot, "*.md", SearchOption.AllDirectories).Select(File.ReadAllLines).ToArray();
        Assert.IsTrue(files.Sum(static lines => lines.Count(static line => line == "{.table}")) >= 40, "The pages style their tables with a line of classes.");
        Assert.AreEqual(files.Sum(static lines => lines.Count(static line => line.StartsWith('|'))), tableRows);
        var worktrees = documentation.ReadPage("worktrees.md")!.ToMarkdown();
        StringAssert.Contains(worktrees, "\n\n| You see | What it means |\n| --- | --- |\n");
        Assert.IsFalse(worktrees.Contains("{.table}", StringComparison.Ordinal));
        // The search reads the same pages.
        Assert.IsTrue(documentation.Search("worktree").Count > 3);
    }

    [GeneratedRegex(@"alta-desktop-[a-z0-9-]+\.webp")]
    private static partial Regex DesktopPicture();

    // A line that is only the attributes Markdig gives the next block: classes, a name, a key.
    [GeneratedRegex(@"^ {0,3}\{[.#:][^{}]*\}\s*$")]
    private static partial Regex SiteAttributes();

    [GeneratedRegex(@"^\s*-\s*\{path:\s*([^,}\s]+)", RegexOptions.Multiline)]
    private static partial Regex MenuEntry();

    // A link of Markdown, or of the HTML of a page, whose target is a page: "name.md" or "folder/name.md", with or without a heading.
    [GeneratedRegex(@"(?:\]\(|href="")((?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+\.md)(?:#[^)""\s]*)?[)""]")]
    private static partial Regex PageLink();
}
