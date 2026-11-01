using CodeAlta.Plugins.Abstractions;

// Settings the user changes in Settings > Extensions: CodeAlta keeps them in config.toml under [plugins.settings-sample],
// finds them in the search of Settings and with Ctrl+P, and gives them to agents with alta settings.
[Plugin("settings-sample", DisplayName = "Build watcher", Description = "Says how often it would look for new builds, from its settings.")]
public sealed class BuildWatcherPlugin : PluginBase
{
    public override IEnumerable<PluginSettingContribution> GetSettings()
    {
        yield return Setting.Integer("poll_minutes", "Check every", "How often new builds are looked for, in minutes.", defaultValue: 5, minimum: 1, maximum: 120)
            with { Group = "Builds", Keywords = ["refresh", "interval"] };
        // Agents read a setting; this one they may also change, when the user asks.
        yield return Setting.Choice("notify", "Tell me when a build", "Which builds raise a toast.", ["failed", "always", "never"], defaultValue: "failed")
            with { Group = "Builds", Agents = PluginSettingAccess.ReadWrite };
        yield return Setting.List("branches", "Branches", "The branches whose builds are watched; empty for every branch.") with { Group = "Filters" };
    }

    public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
    {
        // Told after a change made in Settings or by an agent; a hand edit of config.toml is read at the next read.
        Services.Settings.Changed += OnSettingChanged;
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default)
    {
        Services.Settings.Changed -= OnSettingChanged;
        return ValueTask.CompletedTask;
    }

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        // A value is read when it is used: never kept in a field that a change would leave behind.
        yield return Command.Shell("watcher_status", "Says how the build watcher is set up.", (context, cancellationToken) =>
        {
            var minutes = Services.Settings.GetInt64("poll_minutes") ?? 5;
            var notify = Services.Settings.GetString("notify") ?? "failed";
            var branches = Services.Settings.GetList("branches") ?? [];
            var watched = branches.Count == 0 ? "every branch" : string.Join(", ", branches);
            return new ValueTask<PluginCommandResult>(PluginCommandResult.Message($"Every {minutes} min, toasts for {notify} builds, on {watched}."));
        });
    }

    private void OnSettingChanged(object? sender, PluginSettingChangedEventArgs change)
        => Logger.Info($"The setting {change.Key} was changed by {change.Origin}.");
}
