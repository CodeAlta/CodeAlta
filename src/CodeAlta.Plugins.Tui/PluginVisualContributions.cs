using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugins.Tui;

/// <summary>Optional terminal presentation of portable UI-region content.</summary>
public sealed record PluginVisualContribution : PluginContentContribution
{
    /// <summary>Gets a direct visual, preferred over the native factory without creating a context.</summary>
    public Visual? Visual { get; init; }

    /// <summary>Gets the optional native factory. Native absence or failure never invokes portable fallback.</summary>
    public Func<PluginVisualContext, Visual?>? CreateVisual { get; init; }
}

/// <summary>Renders optional terminal output using the original operation context and cancellation token.</summary>
/// <param name="context">The renderer context.</param>
/// <param name="cancellationToken">The operation cancellation token.</param>
/// <returns>The terminal result, or null for intentional absence.</returns>
public delegate ValueTask<PluginTerminalRenderResult?> PluginTerminalRenderer(PluginRendererContext context, CancellationToken cancellationToken);

/// <summary>Optional terminal renderer with a mandatory inherited portable renderer.</summary>
public sealed record PluginTerminalRendererContribution : PluginRendererContribution
{
    /// <summary>Gets the terminal callback; its null result or exception never triggers portable fallback.</summary>
    public required PluginTerminalRenderer TerminalRenderer { get; init; }
}

/// <summary>A terminal result and its optional Markdown or plain-text representation.</summary>
public sealed record PluginTerminalRenderResult
{
    /// <summary>Gets the native visual.</summary>
    public Visual? Visual { get; init; }
    /// <summary>Gets Markdown content.</summary>
    public string? Markdown { get; init; }
    /// <summary>Gets plain-text content.</summary>
    public string? Text { get; init; }

    /// <summary>Creates a native render result.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="visual"/> is null.</exception>
    public static PluginTerminalRenderResult FromVisual(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        return new PluginTerminalRenderResult { Visual = visual };
    }
}

/// <summary>Factories for optional terminal presentation with mandatory portable callbacks.</summary>
public static class PluginTui
{
    /// <summary>Creates direct native content and its portable fallback.</summary>
    /// <exception cref="ArgumentNullException">Thrown when a visual or callback is null.</exception>
    public static PluginVisualContribution Visual(PluginUiRegion region, Visual visual, Func<PluginVisualContext, PluginRenderResult?> createContent, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(createContent);
        return new PluginVisualContribution { Region = region, Name = name, Order = order, Visual = visual, CreateContent = createContent };
    }

    /// <summary>Creates context-aware native content and its portable fallback.</summary>
    /// <exception cref="ArgumentNullException">Thrown when either callback is null.</exception>
    public static PluginVisualContribution Visual(PluginUiRegion region, Func<PluginVisualContext, Visual?> factory, Func<PluginVisualContext, PluginRenderResult?> createContent, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(createContent);
        return new PluginVisualContribution { Region = region, Name = name, Order = order, CreateVisual = factory, CreateContent = createContent };
    }

    /// <summary>Creates native content from a parameterless factory and its portable fallback.</summary>
    /// <exception cref="ArgumentNullException">Thrown when either callback is null.</exception>
    public static PluginVisualContribution Visual(PluginUiRegion region, Func<Visual?> factory, Func<PluginVisualContext, PluginRenderResult?> createContent, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(createContent);
        return Visual(region, _ => factory(), createContent, name, order);
    }

    /// <summary>Creates an optional terminal renderer and its mandatory portable renderer.</summary>
    /// <exception cref="ArgumentNullException">Thrown when either callback is null.</exception>
    public static PluginTerminalRendererContribution Renderer(PluginUiRegion region, PluginTerminalRenderer terminalRenderer, PluginRenderer renderer, string? target = null, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(terminalRenderer);
        ArgumentNullException.ThrowIfNull(renderer);
        return new PluginTerminalRendererContribution { Region = region, Target = target, Name = name, Order = order, TerminalRenderer = terminalRenderer, Renderer = renderer };
    }
}
