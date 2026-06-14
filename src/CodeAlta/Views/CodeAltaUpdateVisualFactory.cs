using System.Diagnostics;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Views;

internal static class CodeAltaUpdateVisualFactory
{
    internal static Visual CreateToastContent(CodeAltaUpdateCheckSnapshot snapshot, Action<string> copyUpdateCommand)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(copyUpdateCommand);

        var children = new List<Visual>
        {
            new Markup($"CodeAlta {AnsiMarkup.Escape(snapshot.LatestVersionText ?? "?")} is available.")
            {
                HorizontalAlignment = Align.Stretch,
                Wrap = true,
            },
        };
        if (CreateReleaseNotesLink(snapshot.LatestVersionText) is { } releaseNotesLink)
        {
            children.Add(releaseNotesLink);
        }

        children.Add(CreateStretchedUpdateCommandRow(snapshot.UpdateCommand, copyUpdateCommand));

        return new VStack(children.ToArray())
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 1,
        };
    }

    internal static Visual CreateAboutUpdateStatus(CodeAltaUpdateCheckSnapshot snapshot, Action<string> copyUpdateCommand)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(copyUpdateCommand);

        return snapshot.Status switch
        {
            CodeAltaUpdateCheckStatus.Checking => CreateCenteredStatusMarkup($"[info]{TerminalIcons.MdCloudSearchOutline} Checking for updates...[/]"),
            CodeAltaUpdateCheckStatus.Latest => CreateCenteredStatusMarkup($"[success]{TerminalIcons.MdCheckCircleOutline} You are running the latest {CodeAltaApplicationInfo.ProductName} version.[/]"),
            CodeAltaUpdateCheckStatus.UpdateAvailable => CreateUpdateAvailableAboutStatus(snapshot, copyUpdateCommand),
            CodeAltaUpdateCheckStatus.PackageNotFound => CreateCenteredStatusMarkup($"[dim]{TerminalIcons.MdPackageVariantClosed} No published {AnsiMarkup.Escape(snapshot.PackageId)} package was found yet.[/]"),
            CodeAltaUpdateCheckStatus.Failed => CreateCenteredStatusMarkup($"[warning]{TerminalIcons.MdAlertCircleOutline} Update check failed: {AnsiMarkup.Escape(snapshot.ErrorMessage ?? "unknown error")}[/]"),
            _ => CreateCenteredStatusMarkup($"[info]{TerminalIcons.MdInformationOutline} Update status has not been checked yet.[/]"),
        };
    }

    internal static Visual CreateStretchedUpdateCommandRow(string command, Action<string> copyUpdateCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(copyUpdateCommand);

        var commandText = CreateCommandText(command);
        var copyButton = CreateCopyButton(() => copyUpdateCommand(command));
        return new Grid
            {
                ColumnGap = 1,
                HorizontalAlignment = Align.Stretch,
            }
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) },
                new ColumnDefinition { Width = GridLength.Auto })
            .Cell(commandText, 0, 0)
            .Cell(copyButton, 0, 1);
    }

    internal static Visual CreateCenteredUpdateCommandRow(string command, Action<string> copyUpdateCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(copyUpdateCommand);

        return new HStack(
            CreateCommandText(command),
            CreateCopyButton(() => copyUpdateCommand(command)))
        {
            HorizontalAlignment = Align.Center,
            Spacing = 1,
        };
    }

    private static Visual CreateUpdateAvailableAboutStatus(CodeAltaUpdateCheckSnapshot snapshot, Action<string> copyUpdateCommand)
        => new VStack(
            [
                CreateCenteredStatusMarkup($"[warning]{TerminalIcons.MdUpdate} Version {AnsiMarkup.Escape(snapshot.LatestVersionText ?? "?")} is available.[/]"),
                CreateCenteredUpdateCommandRow(snapshot.UpdateCommand, copyUpdateCommand),
            ])
        {
            HorizontalAlignment = Align.Center,
            Spacing = 1,
        };

    private static Visual? CreateReleaseNotesLink(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }

        var uri = $"{AboutDialog.GitHubProjectUri}/releases/tag/{Uri.EscapeDataString(versionText.Trim())}";
        return new Link(uri, "View release notes")
            .Opened((_, e) =>
            {
                TryOpenBrowser(e.Uri);
                e.Handled = true;
            })
            .Tooltip(new TextBlock($"Open {uri}"));
    }

    private static Markup CreateCenteredStatusMarkup(string markup)
        => new(markup)
        {
            HorizontalAlignment = Align.Center,
            Wrap = true,
        };

    private static Markup CreateCommandText(string command)
        => new($"[dim]{AnsiMarkup.Escape(command)}[/]")
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
            Wrap = true,
        };

    private static Visual CreateCopyButton(Action onClick)
    {
        var button = new Button(new TextBlock($"{TerminalIcons.MdContentCopy}")
            {
                IsSelectable = false,
                Wrap = false,
            })
            {
                HorizontalAlignment = Align.End,
                VerticalAlignment = Align.Start,
            }
            .Click(onClick);
        return button.Tooltip(new TextBlock("Copy update command to the clipboard."));
    }

    private static void TryOpenBrowser(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // The Link still renders as an OSC 8 terminal hyperlink when shell launch is unavailable.
        }
    }
}
