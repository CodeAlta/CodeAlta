using System.Diagnostics;
using CodeAlta.Catalog;
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
            new Markup(SR.T("CodeAlta {0} is available.", AnsiMarkup.Escape(snapshot.LatestVersionText ?? "?")))
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
            CodeAltaUpdateCheckStatus.Checking => CreateCenteredStatusMarkup($"[info]{TerminalIcons.MdCloudSearchOutline} {SR.T("Checking for updates...")}[/]"),
            CodeAltaUpdateCheckStatus.Latest => CreateCenteredStatusMarkup($"[success]{TerminalIcons.MdCheckCircleOutline} {SR.T("You are running the latest {0} version.", CodeAltaApplicationInfo.ProductName)}[/]"),
            CodeAltaUpdateCheckStatus.UpdateAvailable => CreateUpdateAvailableAboutStatus(snapshot, copyUpdateCommand),
            CodeAltaUpdateCheckStatus.PackageNotFound => CreateCenteredStatusMarkup($"[dim]{TerminalIcons.MdPackageVariantClosed} {SR.T("No published {0} package was found yet.", AnsiMarkup.Escape(snapshot.PackageId))}[/]"),
            CodeAltaUpdateCheckStatus.Failed => CreateCenteredStatusMarkup($"[warning]{TerminalIcons.MdAlertCircleOutline} {SR.T("Update check failed: {0}", AnsiMarkup.Escape(snapshot.ErrorMessage ?? SR.T("unknown error")))}[/]"),
            _ => CreateCenteredStatusMarkup($"[info]{TerminalIcons.MdInformationOutline} {SR.T("Update status has not been checked yet.")}[/]"),
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
                CreateCenteredStatusMarkup($"[warning]{TerminalIcons.MdUpdate} {SR.T("Version {0} is available.", AnsiMarkup.Escape(snapshot.LatestVersionText ?? "?"))}[/]"),
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
        return new Link(uri, SR.T("View release notes"))
            .Opened((_, e) =>
            {
                TryOpenBrowser(e.Uri);
                e.Handled = true;
            })
            .Tooltip(new TextBlock(SR.T("Open {0}", uri)));
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
        return button.Tooltip(new TextBlock(SR.T("Copy update command to the clipboard.")));
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
