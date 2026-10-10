using System.Text;
using CodeAlta.Plugins.Abstractions;

// A card pinned on the landing page of CodeAlta Desktop: the few things to do today. The plugin keeps the list; the card shows the
// first ones with what is left, a command adds one or ticks the first, and a canvas shows them all.
[Plugin("landing-card", DisplayName = "Landing card", Description = "Pins a card on the landing page: a short list of things to do today.")]
public sealed class LandingCardPlugin : PluginBase
{
    private readonly List<string> _items = [];
    private readonly Lock _gate = new();

    public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        var saved = await Services.State.ReadJsonAsync<List<string>>(PluginStateScope.User, "today", cancellationToken);
        if (saved is not null) lock (_gate) _items.AddRange(saved);
    }

    // The card. The page asks for it each time it is shown, when a command of the plugin ends, and when the plugin says its cards changed.
    public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
    {
        yield return new PluginLandingCardContribution
        {
            Id = "today", Title = "Today", Icon = "list-todo",
            GetCard = (context, _) => ValueTask.FromResult(Card()),
        };
    }

    private PluginLandingCard? Card()
    {
        var items = Items();
        var add = PluginLandingCardAction.RunCommand("Add", "today-add", icon: "plus");
        // A fragment like any other: text is encoded, and an element may run a command of the plugin.
        if (items.Count == 0) return PluginLandingCard.Of($"<p class=\"{PluginHtml.MutedClass}\">Nothing planned for today.</p>", add);
        var html = new StringBuilder("<ul>");
        foreach (var item in items.Take(3)) html.Append("<li>").Append(PluginHtml.Encode(item)).Append("</li>");
        html.Append("</ul>");
        return PluginLandingCard.Of(html.ToString(),
            PluginLandingCardAction.RunCommand("Done", "today-done", icon: "check") with { Primary = true }, add, PluginLandingCardAction.OpenCanvas("All", "today"))
            with { Status = items.Count == 1 ? "1 left" : $"{items.Count} left", Tone = PluginStatusTone.Info };
    }

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("today-add", "Adds something to do today.", async (_, cancellationToken) =>
        {
            var text = await Services.Ui.InputAsync("To do today", cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(text)) return PluginCommandResult.Handled;
            await ChangeAsync(list => list.Add(text.Trim()), cancellationToken);
            return PluginCommandResult.Handled;
        }) with { Label = "Today: add" };
        yield return Command.Shell("today-done", "Ticks the first thing to do today.", async (_, cancellationToken) =>
        {
            await ChangeAsync(static list => { if (list.Count > 0) list.RemoveAt(0); }, cancellationToken);
            return PluginCommandResult.Handled;
        }) with { Label = "Today: first one done" };
    }

    // The whole list, in a tab: the card opens it with an action, and anyone can from the Canvases page.
    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = "today", Title = "Today", Description = "The things to do today.", Icon = "list-todo",
            Open = (_, _) => ValueTask.FromResult(PluginCanvasView.Rendered((_, _) => ValueTask.FromResult(Page()))),
        };
    }

    private string Page()
    {
        var html = new StringBuilder($"<div class=\"{PluginHtml.ColumnClass}\"><h2>Today</h2>");
        var items = Items();
        if (items.Count == 0) html.Append($"<p class=\"{PluginHtml.MutedClass}\">Nothing planned for today.</p>");
        else html.Append("<ol>").AppendJoin(string.Empty, items.Select(static item => $"<li>{PluginHtml.Encode(item)}</li>")).Append("</ol>");
        return html.Append($"<div class=\"{PluginHtml.RowClass}\">").Append(PluginHtml.CommandButton("today-add", "Add", primary: true))
            .Append(PluginHtml.CommandButton("today-done", "First one done")).Append("</div></div>").ToString();
    }

    private List<string> Items()
    {
        lock (_gate) return [.. _items];
    }

    private async ValueTask ChangeAsync(Action<List<string>> change, CancellationToken cancellationToken)
    {
        List<string> snapshot;
        lock (_gate)
        {
            change(_items);
            snapshot = [.. _items];
        }

        await Services.State.WriteJsonAsync(PluginStateScope.User, "today", snapshot, cancellationToken);
        // The card and the tab show the list: the page reads the card again, and the open tabs write their fragment again.
        Services.Ui.InvalidateLandingCards();
        await Services.Canvases.InvalidateAsync("today", cancellationToken);
    }
}
