using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaUpdateVisualFactoryTests
{
    [TestMethod]
    public void CreateToastContent_PutsWrappedCommandOnOwnRowWithCopyButton()
    {
        var snapshot = CreateUpdateAvailableSnapshot();

        var visual = CodeAltaUpdateVisualFactory.CreateToastContent(snapshot, _ => { });

        var root = AssertIsPanel<VStack>(visual);
        Assert.IsTrue(root.Children.Count >= 3, "The toast should put the message, release notes link, and command on separate rows.");

        var message = Assert.IsInstanceOfType<Markup>(root.Children[0]);
        Assert.IsTrue(message.Wrap, "The toast message should wrap.");
        Assert.IsFalse(message.Text?.Contains(snapshot.UpdateCommand, StringComparison.Ordinal) == true, "The command should not be embedded in the message row.");

        var command = FindMarkups(root.Children[^1]).Single(markup => markup.Text?.Contains(snapshot.UpdateCommand, StringComparison.Ordinal) == true);
        Assert.IsTrue(command.Wrap, "The update command should wrap within the toast.");
        AssertCopyButtonExists(root.Children[^1]);
    }

    [TestMethod]
    public void CreateToastContent_AddsReleaseNotesLinkForLatestVersion()
    {
        var snapshot = CreateUpdateAvailableSnapshot();

        var visual = CodeAltaUpdateVisualFactory.CreateToastContent(snapshot, _ => { });

        var link = FindLinks(visual).Single();
        Assert.AreEqual("View release notes", link.Text);
        Assert.AreEqual("https://github.com/CodeAlta/CodeAlta/releases/tag/0.9.2", link.Uri);
    }

    [TestMethod]
    public void CreateAboutUpdateStatus_AddsCopyButtonWhenUpdateIsAvailable()
    {
        var snapshot = CreateUpdateAvailableSnapshot();

        var visual = CodeAltaUpdateVisualFactory.CreateAboutUpdateStatus(snapshot, _ => { });

        var command = FindMarkups(visual).Single(markup => markup.Text?.Contains(snapshot.UpdateCommand, StringComparison.Ordinal) == true);
        Assert.IsTrue(command.Wrap, "The about dialog update command should wrap.");
        var root = AssertIsPanel<VStack>(visual);
        Assert.AreEqual(Align.Center, root.HorizontalAlignment, "The about dialog update block should align with the centered about text block.");
        Assert.AreEqual(Align.Center, root.Children[1].HorizontalAlignment, "The about dialog command row should not stretch across the whole dialog.");
        AssertCopyButtonExists(visual);
    }

    [TestMethod]
    public void CreateAboutUpdateStatus_DoesNotAddCopyButtonWithoutUpdateCommand()
    {
        var snapshot = CreateUpdateAvailableSnapshot() with
        {
            Status = CodeAltaUpdateCheckStatus.Latest,
            LatestVersionText = null,
        };

        var visual = CodeAltaUpdateVisualFactory.CreateAboutUpdateStatus(snapshot, _ => { });

        Assert.IsFalse(FindButtons(visual).Any(IsCopyButton));
    }

    private static CodeAltaUpdateCheckSnapshot CreateUpdateAvailableSnapshot()
        => new(
            CodeAltaUpdateCheckStatus.UpdateAvailable,
            "CodeAlta",
            "0.9.1",
            "0.9.2",
            LatestVersionIsPrerelease: false,
            IncludePrerelease: false,
            ErrorMessage: null);

    private static T AssertIsPanel<T>(Visual visual)
        where T : Panel
        => Assert.IsInstanceOfType<T>(visual);

    private static void AssertCopyButtonExists(Visual visual)
        => Assert.IsTrue(FindButtons(visual).Any(IsCopyButton), "Expected a copy-to-clipboard button for the update command.");

    private static bool IsCopyButton(Button button)
        => button.Content is TextBlock textBlock && textBlock.Text == $"{TerminalIcons.MdContentCopy}";

    private static IEnumerable<Markup> FindMarkups(Visual? visual)
    {
        if (visual is null)
        {
            yield break;
        }

        if (visual is Markup markup)
        {
            yield return markup;
        }

        foreach (var child in EnumerateChildren(visual))
        {
            foreach (var childMarkup in FindMarkups(child))
            {
                yield return childMarkup;
            }
        }
    }

    private static IEnumerable<Button> FindButtons(Visual? visual)
    {
        if (visual is null)
        {
            yield break;
        }

        if (visual is Button button)
        {
            yield return button;
        }

        foreach (var child in EnumerateChildren(visual))
        {
            foreach (var childButton in FindButtons(child))
            {
                yield return childButton;
            }
        }
    }

    private static IEnumerable<Link> FindLinks(Visual? visual)
    {
        if (visual is null)
        {
            yield break;
        }

        if (visual is Link link)
        {
            yield return link;
        }

        foreach (var child in EnumerateChildren(visual))
        {
            foreach (var childLink in FindLinks(child))
            {
                yield return childLink;
            }
        }
    }

    private static IEnumerable<Visual> EnumerateChildren(Visual visual)
    {
        if (visual is ContentVisual contentVisual && contentVisual.Content is { } content)
        {
            yield return content;
        }

        if (visual is Panel panel)
        {
            foreach (var child in panel)
            {
                yield return child;
            }
        }

        if (visual is Grid grid)
        {
            foreach (var cell in grid.Cells)
            {
                yield return cell;
            }
        }
    }
}
