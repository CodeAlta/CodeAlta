using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;

namespace CodeAlta.Tui.Plugins;

// Borrows the shared adapter; owns no runtime, services, tasks, or terminal lifetime.
internal sealed class TerminalPluginContributionAdapter(PluginContributionAdapterService adapter)
{
    private readonly PluginContributionAdapterService _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    internal IReadOnlyList<Visual> CreateVisuals(IReadOnlyList<ActivePluginInstance> activePlugins, PluginUiRegion region, PluginAdapterOperationOptions? options)
        => _adapter.CreateContent<Visual>(activePlugins, region, (content, createContext) =>
        {
            ArgumentNullException.ThrowIfNull(content.CreateContent);
            var terminal = content as PluginVisualContribution;
            return SelectContent(options?.SupportsTerminalVisuals == true, terminal is not null,
                terminal?.Visual, terminal?.CreateVisual, context => Materialize(content.CreateContent(context)), createContext);
        }, options);

    internal async ValueTask<(IReadOnlyList<PluginTerminalRenderResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> RenderAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins, PluginUiRegion region, string? target, object? payload,
        PluginAdapterOperationOptions? options, CancellationToken cancellationToken)
        => await _adapter.RenderAsync<PluginTerminalRenderResult>(activePlugins, region, target, payload, (renderer, context, token) =>
        {
            ArgumentNullException.ThrowIfNull(renderer.Renderer);
            var terminal = renderer as PluginTerminalRendererContribution;
            return SelectRenderer<PluginRendererContext, PluginTerminalRenderResult>(options?.SupportsTerminalVisuals == true,
                terminal is null ? null : (originalContext, originalToken) => terminal.TerminalRenderer(originalContext, originalToken),
                (originalContext, originalToken) => RenderPortableAsync(renderer.Renderer, originalContext, originalToken), context, token);
        }, options, cancellationToken).ConfigureAwait(false);

    internal static TResult? SelectContent<TContext, TResult>(bool supportsTerminal, bool hasTerminalPresentation, TResult? direct,
        Func<TContext, TResult?>? createNative, Func<TContext, TResult?> createPortable, Func<TContext> createContext) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(createPortable);
        ArgumentNullException.ThrowIfNull(createContext);
        return supportsTerminal && hasTerminalPresentation ? direct ?? createNative?.Invoke(createContext()) : createPortable(createContext());
    }

    internal static ValueTask<TResult?> SelectRenderer<TContext, TResult>(bool supportsTerminal,
        Func<TContext, CancellationToken, ValueTask<TResult?>>? renderNative,
        Func<TContext, CancellationToken, ValueTask<TResult?>> renderPortable, TContext context, CancellationToken cancellationToken) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(renderPortable);
        return supportsTerminal && renderNative is not null ? renderNative(context, cancellationToken) : renderPortable(context, cancellationToken);
    }

    private static Visual? Materialize(PluginRenderResult? content)
        => content is null ? null : content.Markdown is { } markdown ? new MarkdownControl(markdown)
            : content.Text is { } text ? new TextBlock(text) : null;

    private static async ValueTask<PluginTerminalRenderResult?> RenderPortableAsync(PluginRenderer renderer, PluginRendererContext context, CancellationToken cancellationToken)
    {
        var result = await renderer(context, cancellationToken).ConfigureAwait(false);
        return result is null ? null : new PluginTerminalRenderResult { Markdown = result.Markdown, Text = result.Text };
    }
}
