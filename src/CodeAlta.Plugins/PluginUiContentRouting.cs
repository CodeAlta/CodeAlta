using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

// These are the production traversal cores. Owners are supplied by the adapter's existing active lookup.
internal static class PluginUiContentRouting
{
    internal static IReadOnlyList<TResult> CreateContent<TOwner, TResult>(
        IReadOnlyList<PluginContributionRegistration> registrations,
        PluginUiRegion region,
        PluginAdapterOperationOptions? options,
        Func<PluginContributionRegistration, TOwner?> getActive,
        Func<PluginContributionRegistration, PluginContentContribution, TOwner, TResult?> materialize)
        where TOwner : class where TResult : class
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(getActive);
        ArgumentNullException.ThrowIfNull(materialize);
        if (options is not null && (options.IsHeadless || !options.HasInteractiveUi)) return [];
        var results = new List<TResult>();
        foreach (var registration in registrations)
        {
            if (registration.Contribution is not PluginContentContribution content || content.Region != region ||
                getActive(registration) is not { } active) continue;
            var result = materialize(registration, content, active);
            if (result is not null) results.Add(result);
        }
        return results;
    }

    internal static ValueTask<(IReadOnlyList<TResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> RenderAsync<TOwner, TResult>(
        IReadOnlyList<PluginContributionRegistration> registrations,
        PluginUiRegion region,
        string? target,
        PluginAdapterOperationOptions? options,
        Func<PluginContributionRegistration, (TOwner Owner, PluginRendererContext Context)?> createContext,
        Func<PluginRendererContribution, PluginRendererContext, CancellationToken, ValueTask<TResult?>> render,
        Func<PluginContributionRegistration, TOwner, Exception, PluginRuntimeDiagnostic> reportFailure,
        CancellationToken cancellationToken)
        where TOwner : class where TResult : class
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(createContext);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(reportFailure);
        return InvokeAsync();

        async ValueTask<(IReadOnlyList<TResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> InvokeAsync()
        {
            if (options is not null && (options.IsHeadless || !options.HasInteractiveUi)) return ([], []);
            var results = new List<TResult>();
            var diagnostics = new List<PluginRuntimeDiagnostic>();
            foreach (var registration in registrations)
            {
                if (registration.Contribution is not PluginRendererContribution renderer || renderer.Region != region ||
                    !(string.IsNullOrWhiteSpace(renderer.Target) || string.Equals(renderer.Target, target, StringComparison.OrdinalIgnoreCase))) continue;
                // Context creation (including active lookup) deliberately remains outside the callback catch.
                var operation = createContext(registration);
                if (operation is not { } selected) continue;
                var (active, context) = selected;
                try
                {
                    var result = await render(renderer, context, cancellationToken).ConfigureAwait(false);
                    context.Invalidate();
                    if (result is not null) results.Add(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diagnostics.Add(reportFailure(registration, active, ex));
                }
            }
            return (results, diagnostics);
        }
    }
}
