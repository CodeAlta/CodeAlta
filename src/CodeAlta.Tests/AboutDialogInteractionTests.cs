using System.Reflection;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AboutDialogInteractionTests
{
    [TestMethod]
    public void AboutDialog_EscapeClosesDialog()
    {
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var root = new Button("Root");
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });
        var dialog = new AboutDialog(
            () => new Rectangle(0, 0, 120, 40),
            () => root,
            new State<float>(0f));

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            app.Focus(root);
            dialog.Show();
            TickTerminalApp(app);

            Assert.IsTrue(IsDialogOpen(dialog));

            var backend = (InMemoryTerminalBackend)session.Instance.Backend;
            backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Escape });
            TickTerminalApp(app);

            Assert.IsFalse(IsDialogOpen(dialog), "The about dialog should close when Escape is pressed.");
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void AboutDialog_HasCloseButton()
    {
        var dialog = new AboutDialog(
            () => new Rectangle(0, 0, 120, 40),
            () => null,
            new State<float>(0f));

        Assert.IsInstanceOfType<Button>(GetDialog(dialog).TopRightText);
    }

    [TestMethod]
    public void AboutDialog_IncludesProjectAndWebsiteLinks()
    {
        var dialog = new AboutDialog(
            () => new Rectangle(0, 0, 120, 40),
            () => null,
            new State<float>(0f));

        var links = FindLinks(GetDialog(dialog).Content).ToArray();

        Assert.IsTrue(links.Any(link =>
            link.Uri == AboutDialog.GitHubProjectUri &&
            link.Text?.Contains("GitHub", StringComparison.Ordinal) == true));
        Assert.IsTrue(links.Any(link =>
            link.Uri == AboutDialog.WebsiteUri &&
            link.Text?.Contains("Website", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public void AboutDialog_UpdateAvailableStatusUsesWrappedCommandRowWithCopyButton()
    {
        using var updateService = new CodeAltaUpdateService();
        var snapshot = new CodeAltaUpdateCheckSnapshot(
            CodeAltaUpdateCheckStatus.UpdateAvailable,
            "CodeAlta",
            "0.9.1",
            "0.9.2",
            LatestVersionIsPrerelease: false,
            IncludePrerelease: false,
            ErrorMessage: null);
        typeof(CodeAltaUpdateService)
            .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(updateService, snapshot);
        var dialog = new AboutDialog(
            () => new Rectangle(0, 0, 120, 40),
            () => null,
            new State<float>(0f),
            updateService);

        var updateStatus = (Visual)typeof(AboutDialog)
            .GetMethod("BuildUpdateStatusVisual", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null)!;

        var command = FindMarkups(updateStatus).Single(markup => markup.Text?.Contains(snapshot.UpdateCommand, StringComparison.Ordinal) == true);
        Assert.IsTrue(command.Wrap, "The about dialog update command should wrap.");
        Assert.IsTrue(FindButtons(updateStatus).Any(IsCopyButton), "Expected a copy-to-clipboard button for the update command.");
    }

    private static bool IsDialogOpen(AboutDialog dialog)
        => GetDialog(dialog).App is not null;

    private static Dialog GetDialog(AboutDialog dialog)
        => (Dialog)typeof(AboutDialog)
            .GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!;

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

        if (visual is ContentVisual contentVisual)
        {
            foreach (var childLink in FindLinks(contentVisual.Content))
            {
                yield return childLink;
            }
        }

        if (visual is Panel panel)
        {
            foreach (var child in panel)
            {
                foreach (var childLink in FindLinks(child))
                {
                    yield return childLink;
                }
            }
        }
    }

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

    private static bool IsCopyButton(Button button)
        => button.Content is TextBlock textBlock && textBlock.Text == $"{TerminalIcons.MdContentCopy}";

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

    private static void TickTerminalApp(TerminalApp app)
        => typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [null]);

    private static void InvokeTerminalApp(TerminalApp app, string methodName)
        => typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
}
