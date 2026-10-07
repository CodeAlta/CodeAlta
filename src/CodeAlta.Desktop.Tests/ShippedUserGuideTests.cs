using System.Text.RegularExpressions;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Desktop.Tests;

/// <summary>The user guide of the site ships beside the desktop application, where its agents read it.</summary>
[TestClass]
public sealed partial class ShippedUserGuideTests
{
    [TestMethod]
    public void TheUserGuide_IsBesideTheApplication_WithThePicturesOfTheDesktopItsPagesShow()
    {
        var roots = new FileSystemPromptContentLocator().GetRoots(new SystemPromptDiscoveryContext());
        var guide = roots.ShippedUserGuideRoot;
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
    }

    [GeneratedRegex(@"alta-desktop-[a-z0-9-]+\.webp")]
    private static partial Regex DesktopPicture();
}
