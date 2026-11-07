using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.LiveTool;

// `alta release-notes`: the notes of the versions of CodeAlta, as GitHub publishes them, and where the running version
// stands. Reading them changes nothing; `--show` opens their dialog in the window, which changes nothing either.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] ReleaseNotesPolicies = [Read("release-notes")];

    private static Command CreateReleaseNotesCommand(AltaCommandContext context)
    {
        string? version = null;
        bool list = false, show = false, refresh = false;
        var command = Leaf("release-notes", "Read the release notes of CodeAlta: the running version and the latest one, the notes of a version, the list of versions, or show them to the user.");
        command.Add("<version>?", "The version whose notes are read or shown; the running one by default.", value => version = value);
        command.Add("list", "List the published versions, the newest first, without their notes.", value => list = value is not null);
        command.Add("show", "Show the notes to the user, in their dialog in the window, instead of printing them.", value => show = value is not null);
        command.Add("refresh", "Read the releases from GitHub again rather than the ones read a moment ago.", value => refresh = value is not null);
        command.Add((_, _) => new ValueTask<int>(HandleReleaseNotesAsync(context, NormalizeOptionalText(version), list, show, refresh)));
        AddHelpText(command,
            "It prints an `alta.release-notes.status` record (the running version, the latest one, whether it is the latest), then the release of the version with its notes in Markdown (`notes`), or with --list one record per version.",
            "The notes come from the releases of github.com/CodeAlta/CodeAlta; a development build has none of its own.",
            "Examples: `alta release-notes`; `alta release-notes 4.1.0`; `alta release-notes --list`; `alta release-notes --show`.");
        return command;
    }

    private static async Task<int> HandleReleaseNotesAsync(AltaCommandContext context, string? version, bool list, bool show, bool refresh)
    {
        const string CommandPath = "alta release-notes";
        var notes = context.Services.Get<IAltaReleaseNotes>();
        if (notes is null)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable, "The release notes are read in CodeAlta Desktop.");
            return AltaExitCodes.ServiceUnavailable;
        }

        if (version is not null && (version.Length > 64 || version.Any(char.IsWhiteSpace) || version.Any(char.IsControl)))
            return UsageError(context, "usage.invalidVersion", "A version is written as its tag, such as 4.1.0.", CommandPath);
        if (show && list) return UsageError(context, "usage.conflict", "--show and --list do not go together.", CommandPath);
        if (show)
        {
            if (notes.Show(version, context.Caller.SourceSessionId))
            {
                WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "alta.release-notes.shown", ["version"] = 1, ["correlationId"] = context.CorrelationId, ["release"] = version,
                });
                return AltaExitCodes.Success;
            }

            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "view.unavailable", AltaExitCodes.ServiceUnavailable, "No CodeAlta window is open to show the release notes.");
            return AltaExitCodes.ServiceUnavailable;
        }

        AltaReleaseNotesList releases;
        try
        {
            releases = await notes.ListAsync(refresh, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "release-notes.unavailable", AltaExitCodes.ServiceUnavailable,
                $"The release notes could not be read from GitHub: {exception.Message}");
            return AltaExitCodes.ServiceUnavailable;
        }

        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.release-notes.status", ["version"] = 1, ["correlationId"] = context.CorrelationId,
            ["current"] = releases.CurrentVersion, ["latest"] = releases.LatestVersion, ["isLatest"] = releases.IsLatest,
        });
        if (list)
        {
            foreach (var release in releases.Releases) WriteRelease(context, release, withNotes: false);
            WriteSummary(context, "alta.release-notes.summary", releases.Releases.Count, truncated: false);
            return AltaExitCodes.Success;
        }

        var wanted = version ?? releases.CurrentVersion;
        var found = releases.Releases.FirstOrDefault(release => SameReleaseVersion(release.Version, wanted));
        if (found is null)
        {
            return NotFound(context, "release-notes.notFound", version is null
                ? $"The running version {releases.CurrentVersion} has no release notes of its own (a development build). `alta release-notes --list` lists the published versions."
                : $"No release of version {version} is published. `alta release-notes --list` lists the published versions.");
        }

        WriteRelease(context, found, withNotes: true);
        return AltaExitCodes.Success;
    }

    private static void WriteRelease(AltaCommandContext context, AltaRelease release, bool withNotes)
        => WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.release-notes.release", ["version"] = 1, ["correlationId"] = context.CorrelationId,
            ["release"] = release.Version, ["name"] = release.Name == release.Version ? null : release.Name,
            ["publishedAt"] = release.PublishedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["prerelease"] = release.Prerelease ? true : null, ["url"] = release.Url, ["notes"] = withNotes ? release.Body : null,
        });

    private static bool SameReleaseVersion(string left, string right)
        => string.Equals(left.TrimStart('v', 'V'), right.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);
}
