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

    /// <summary>Creates a custom terminal dialog request, not a guarantee of host presentation.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="content">The custom terminal content.</param>
    /// <returns>The terminal request. Unsupported hosts may ignore it or return no result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    public static PluginTerminalDialogRequest CustomDialog(string title, Visual content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new PluginTerminalDialogRequest
        {
            Kind = PluginDialogKind.Custom,
            Title = title,
            Content = content,
        };
    }

    /// <summary>Creates a prompt contribution that attaches only to a terminal prompt host.</summary>
    /// <param name="name">The contribution name.</param>
    /// <param name="attach">The deferred attachment callback.</param>
    /// <param name="placeholderText">Optional guidance for hosts where the contribution applies.</param>
    /// <param name="order">The ordering hint.</param>
    /// <returns>A neutral contribution whose attachment is declined on unsupported hosts.</returns>
    /// <remarks>
    /// A null attachment does not advertise a supported action. The caller owns any returned attachment;
    /// this factory does not dispose it. Native null results and exceptions never invoke a fallback.
    /// The returned attach handler rejects a null host before checking terminal support.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> or <paramref name="attach"/> is null, or the returned handler receives a null host.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty or whitespace.</exception>
    public static PluginPromptEditorContribution PromptEditor(string name, Func<IPluginTerminalPromptEditorHost, IAsyncDisposable?> attach, string? placeholderText = null, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(attach);
        return new PluginPromptEditorContribution
        {
            Name = name,
            PlaceholderText = placeholderText,
            Order = order,
            Attach = host =>
            {
                ArgumentNullException.ThrowIfNull(host);
                return host is IPluginTerminalPromptEditorHost terminal ? attach(terminal) : null;
            },
        };
    }
}
