namespace CodeAlta.Plugins;

public sealed partial class PluginRuntimeManager
{
    /// <summary>
    /// Gets the names of the tables a plugin keeps in the application database, for the confirmation that names them
    /// before the plugin is removed or its data is reset.
    /// </summary>
    /// <param name="pluginRuntimeKey">The runtime key of the plugin, such as <c>builtin:statistics</c>.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The table names in alphabetical order; empty when the plugin has none or the host has no database.</returns>
    /// <exception cref="ArgumentException"><paramref name="pluginRuntimeKey"/> is empty.</exception>
    public async ValueTask<IReadOnlyList<string>> ListPluginTablesAsync(string pluginRuntimeKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        if (ApplicationDatabase is not { } database)
        {
            return [];
        }

        return await database.ListTablesAsync(PluginDatabase.CreateTablePrefix(pluginRuntimeKey), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops every table of a plugin from the application database and forgets the version recorded for it: its next
    /// migration starts from nothing. The plugin should be deactivated first; a plugin that goes on running finds its
    /// tables gone.
    /// </summary>
    /// <param name="pluginRuntimeKey">The runtime key of the plugin.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The names of the tables that were dropped; empty when the plugin had none or the host has no database.</returns>
    /// <exception cref="ArgumentException"><paramref name="pluginRuntimeKey"/> is empty.</exception>
    public async ValueTask<IReadOnlyList<string>> DropPluginTablesAsync(string pluginRuntimeKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        if (ApplicationDatabase is not { } database)
        {
            return [];
        }

        return await database.DropTablesAsync(PluginDatabase.GetOwner(pluginRuntimeKey), PluginDatabase.CreateTablePrefix(pluginRuntimeKey), cancellationToken).ConfigureAwait(false);
    }
}
