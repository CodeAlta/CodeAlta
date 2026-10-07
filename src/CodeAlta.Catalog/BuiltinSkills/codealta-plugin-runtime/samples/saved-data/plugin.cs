using CodeAlta.Plugins.Abstractions;

// Data kept between the runs of CodeAlta: Services.State stores JSON files in a folder of the plugin.
[Plugin("saved-data", DisplayName = "Counter", Description = "Counts how often its command ran, across restarts.")]
public sealed class CounterPlugin : PluginBase
{
    private Counter _counter = new(0, null);

    // Read once when the plugin starts (a reload starts it again), before its contributions are collected.
    public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => _counter = await Services.State.ReadJsonAsync<Counter>(PluginStateScope.User, "counter", cancellationToken) ?? _counter;

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("count", "Counts one more run.", async (context, cancellationToken) =>
        {
            _counter = new(_counter.Runs + 1, DateTimeOffset.Now);
            // PluginStateScope.Project keeps the data in the project instead: <project>/.alta/plugin-data/.
            await Services.State.WriteJsonAsync(PluginStateScope.User, "counter", _counter, cancellationToken);
            return PluginCommandResult.Message($"Ran {_counter.Runs} times.");
        });
    }

    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        // A callback like this one runs often: it reads the field, never the file.
        yield return new PluginStatusContribution
        {
            Region = PluginUiRegion.SessionStatus,
            Name = "runs",
            GetStatus = _ => new PluginStatusItem { Label = "Runs", Text = _counter.Runs.ToString(), Command = "count" },
        };
    }
}

// What is stored: public properties, as System.Text.Json writes them.
public sealed record Counter(int Runs, DateTimeOffset? LastRun);
