using System.Globalization;
using System.Text;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Statistics;

public sealed partial class StatisticsPlugin
{
    /// <summary>The identifier of the card the plugin pins on the landing page.</summary>
    internal const string LandingCardId = "overview";

    /// <summary>The period of the figures of the card.</summary>
    internal const string LandingPeriod = "7d";

    private int _landingState = -1;

    /// <inheritdoc />
    public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
    {
        if (!ReadsSessions)
        {
            yield break;
        }

        yield return new PluginLandingCardContribution
        {
            Id = LandingCardId,
            Title = "Statistics",
            Icon = "chart-column",
            Order = -100,
            GetCard = GetLandingCardAsync,
        };
    }

    // The card follows the state of the history: it is asked again when the state changes, not for each number of a reading in progress.
    internal void OnLandingStatusChanged(StatisticsStatus status)
    {
        var state = (int)status.State;
        if (Interlocked.Exchange(ref _landingState, state) != state)
        {
            Services.Ui.InvalidateLandingCards();
        }
    }

    /// <summary>
    /// Writes the card of the landing page: a few figures of the last days for the space of the page. It reads what the statistics hold. It
    /// never chooses how much history to read and never starts a reading: that is asked in the canvas, which the card opens.
    /// </summary>
    internal async ValueTask<PluginLandingCard?> GetLandingCardAsync(PluginLandingCardContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var open = PluginLandingCardAction.OpenCanvas("Open Statistics", CanvasId, icon: "chart-column") with { Primary = true };
        PluginLandingCard Note(string text) => PluginLandingCard.Of($"<p class=\"{PluginHtml.MutedClass}\">{PluginHtml.Encode(text)}</p>", open);

        var engine = _engine;
        var status = engine?.Status;
        switch (status?.State)
        {
            case null or HistoryState.Starting:
                return Note("The statistics are starting.");
            case HistoryState.NeedsChoice:
                return Note("Choose in Statistics how much of your history to read.") with { Status = "Not set up", Tone = PluginStatusTone.Warning };
            case HistoryState.Failed:
                return Note("The statistics could not start.") with { Status = "Unavailable", Tone = PluginStatusTone.Error };
        }

        var summary = await engine!.Queries.SummaryAsync(new StatisticsRequest { Period = LandingPeriod, Filter = await SpaceFilterAsync(context.SpaceId, cancellationToken).ConfigureAwait(false) },
            cancellationToken).ConfigureAwait(false);
        double Tile(string id) => summary.Tiles.FirstOrDefault(tile => string.Equals(tile.Id, id, StringComparison.Ordinal))?.Value ?? 0d;
        var reading = status!.State == HistoryState.Reading;
        var sessions = Tile("sessions");
        var prompts = Tile("your-prompts");
        var tokens = Tile("tokens");
        var active = Tile("active-time");
        if (sessions <= 0 && prompts <= 0 && tokens <= 0 && active <= 0)
        {
            return Note(reading ? "Reading your history. The figures appear as it is read." : "No activity in the last 7 days.")
                with { Status = reading ? "Reading history" : "Last 7 days", Tone = PluginStatusTone.Muted };
        }

        var html = new StringBuilder($"<div class=\"{PluginHtml.RowClass}\">")
            .Append(PluginHtml.Stat(Compact(sessions), "Sessions"))
            .Append(PluginHtml.Stat(Compact(prompts), "Your prompts"))
            .Append(PluginHtml.Stat(Compact(tokens), "Tokens"))
            .Append(PluginHtml.Stat(FormatDuration(TimeSpan.FromMilliseconds(active)), "Active time"))
            .Append("</div>");
        return PluginLandingCard.Of(html.ToString(), open) with { Status = reading ? "Reading history" : "Last 7 days", Tone = PluginStatusTone.Muted };
    }

    // The space of the page as a filter. The space that holds every project filters nothing, and neither does one the directory does not know.
    private async ValueTask<StatisticsFilter> SpaceFilterAsync(string? spaceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spaceId) || _directory is not { } directory)
        {
            return new StatisticsFilter();
        }

        var spaces = await directory.ListSpacesAsync(cancellationToken).ConfigureAwait(false);
        var space = spaces.FirstOrDefault(item => string.Equals(item.Id, spaceId, StringComparison.Ordinal));
        return space is null || space.IsDefault ? new StatisticsFilter() : new StatisticsFilter { Space = space.Id };
    }

    /// <summary>Writes a count in a few characters: <c>950</c>, <c>12.3K</c>, <c>4.1M</c>, <c>1.2B</c>.</summary>
    internal static string Compact(double value)
    {
        var rounded = Math.Round(Math.Max(0d, value));
        if (rounded < 1e4)
        {
            return rounded.ToString("#,0", CultureInfo.InvariantCulture);
        }

        // The unit is chosen for the number as it is written: 999,960 is 1M, not 1000K.
        var thousands = Math.Round(rounded / 1e3, 1);
        if (thousands < 1000d)
        {
            return thousands.ToString("0.#", CultureInfo.InvariantCulture) + "K";
        }

        var millions = Math.Round(rounded / 1e6, 1);
        return millions < 1000d
            ? millions.ToString("0.#", CultureInfo.InvariantCulture) + "M"
            : Math.Round(rounded / 1e9, 1).ToString("0.#", CultureInfo.InvariantCulture) + "B";
    }
}
