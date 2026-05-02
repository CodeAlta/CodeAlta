using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>Base type for UI contributions.</summary>
public abstract record PluginUiContribution
{
    /// <summary>Gets the UI region.</summary>
    public required PluginUiRegion Region { get; init; }

    /// <summary>Gets the ordering hint.</summary>
    public int Order { get; init; }

    /// <summary>Gets an optional natural name.</summary>
    public string? Name { get; init; }
}

/// <summary>Identifies a plugin UI region.</summary>
public enum PluginUiRegion
{
    /// <summary>Command palette entries or providers.</summary>
    CommandPalette,
    /// <summary>Command bar controls.</summary>
    CommandBar,
    /// <summary>Prompt toolbar controls.</summary>
    PromptToolbar,
    /// <summary>Prompt autocomplete providers.</summary>
    PromptAutocomplete,
    /// <summary>Prompt attachment renderers.</summary>
    PromptAttachmentRenderer,
    /// <summary>Thread footer or status area.</summary>
    ThreadFooter,
    /// <summary>Thread status area.</summary>
    ThreadStatus,
    /// <summary>Thread header area.</summary>
    ThreadHeader,
    /// <summary>Sidebar section.</summary>
    SidebarSection,
    /// <summary>Timeline message renderer.</summary>
    TimelineMessageRenderer,
    /// <summary>Tool-call renderer.</summary>
    ToolCallRenderer,
    /// <summary>Settings or management page.</summary>
    SettingsPage,
    /// <summary>Modal dialog factory.</summary>
    ModalDialog,
    /// <summary>Editor tab provider.</summary>
    EditorTab,
}

/// <summary>Describes a visual UI contribution.</summary>
public sealed record PluginVisualContribution : PluginUiContribution
{
    /// <summary>Gets a direct visual supplied by the plugin.</summary>
    public Visual? Visual { get; init; }

    /// <summary>Gets a factory used to create or rebuild the visual.</summary>
    public Func<PluginVisualContext, Visual?>? CreateVisual { get; init; }
}

/// <summary>Describes a status UI contribution.</summary>
public sealed record PluginStatusContribution : PluginUiContribution
{
    /// <summary>Gets the status provider.</summary>
    public required Func<PluginStatusContext, PluginStatusItem?> GetStatus { get; init; }
}

/// <summary>Describes a plugin status item.</summary>
public sealed record PluginStatusItem
{
    /// <summary>Gets the status label.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the status text.</summary>
    public required string Text { get; init; }

    /// <summary>Gets optional icon or markup text.</summary>
    public string? IconMarkup { get; init; }

    /// <summary>Gets the status tone.</summary>
    public PluginStatusTone Tone { get; init; } = PluginStatusTone.Info;
}

/// <summary>Identifies status tone.</summary>
public enum PluginStatusTone
{
    /// <summary>Informational tone.</summary>
    Info,
    /// <summary>Success tone.</summary>
    Success,
    /// <summary>Warning tone.</summary>
    Warning,
    /// <summary>Error tone.</summary>
    Error,
    /// <summary>Muted tone.</summary>
    Muted,
}

/// <summary>Describes a renderer contribution.</summary>
public sealed record PluginRendererContribution : PluginUiContribution
{
    /// <summary>Gets the renderer target kind or schema.</summary>
    public string? Target { get; init; }

    /// <summary>Gets the renderer callback.</summary>
    public required PluginRenderer Renderer { get; init; }
}

/// <summary>Represents a renderer result.</summary>
public sealed record PluginRenderResult
{
    /// <summary>Gets an optional rendered visual.</summary>
    public Visual? Visual { get; init; }

    /// <summary>Gets optional markdown content.</summary>
    public string? Markdown { get; init; }

    /// <summary>Gets optional plain text fallback content.</summary>
    public string? Text { get; init; }

    /// <summary>Creates a visual render result.</summary>
    /// <param name="visual">The visual.</param>
    /// <returns>The render result.</returns>
    public static PluginRenderResult FromVisual(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        return new PluginRenderResult { Visual = visual };
    }

    /// <summary>Creates a markdown render result.</summary>
    /// <param name="markdown">The markdown content.</param>
    /// <returns>The render result.</returns>
    public static PluginRenderResult FromMarkdown(string markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        return new PluginRenderResult { Markdown = markdown };
    }
}

/// <summary>Describes a resource contribution.</summary>
public sealed record PluginResourceContribution
{
    /// <summary>Gets the resource kind.</summary>
    public required PluginResourceKind Kind { get; init; }

    /// <summary>Gets the resource path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the resource precedence.</summary>
    public int Precedence { get; init; }

    /// <summary>Gets a value indicating whether <see cref="Path"/> is relative to the plugin package directory.</summary>
    public bool IsPackageRelative { get; init; } = true;

    /// <summary>Gets optional source metadata.</summary>
    public string? Source { get; init; }
}

/// <summary>Identifies a plugin resource kind.</summary>
public enum PluginResourceKind
{
    /// <summary>Skill root directory.</summary>
    SkillRoot,
    /// <summary>System prompt root directory.</summary>
    SystemPromptRoot,
    /// <summary>Template root directory.</summary>
    TemplateRoot,
    /// <summary>Theme root directory.</summary>
    ThemeRoot,
    /// <summary>MCP server manifest file.</summary>
    McpServerManifest,
    /// <summary>Agent definition root directory.</summary>
    AgentDefinitionRoot,
    /// <summary>Other plugin resource.</summary>
    Other,
}

/// <summary>Describes compaction hook capabilities.</summary>
[Flags]
public enum PluginCompactionCapabilities
{
    /// <summary>No declared capabilities.</summary>
    None = 0,
    /// <summary>Can observe compaction.</summary>
    Observe = 1 << 0,
    /// <summary>Can contribute instructions.</summary>
    Instructions = 1 << 1,
    /// <summary>Can reduce plugin-owned payloads.</summary>
    Reduce = 1 << 2,
    /// <summary>Can cancel compaction before it starts.</summary>
    Cancel = 1 << 3,
    /// <summary>May provide a replacement compaction provider in a future runtime.</summary>
    ReplacementProvider = 1 << 4,
}

/// <summary>Describes a compaction contribution.</summary>
public sealed record PluginCompactionContribution
{
    /// <summary>Gets the ordering hint.</summary>
    public int Order { get; init; }

    /// <summary>Gets declared capabilities.</summary>
    public PluginCompactionCapabilities Capabilities { get; init; }

    /// <summary>Gets the before-compaction hook.</summary>
    public PluginBeforeCompactionHandler? BeforeCompaction { get; init; }

    /// <summary>Gets the instruction provider.</summary>
    public PluginCompactionInstructionProvider? Instructions { get; init; }

    /// <summary>Gets the reducer callback.</summary>
    public PluginCompactionReducer? Reducer { get; init; }

    /// <summary>Gets the after-compaction hook.</summary>
    public PluginAfterCompactionHandler? AfterCompaction { get; init; }
}

/// <summary>Describes a before-compaction result.</summary>
public sealed record PluginBeforeCompactionResult
{
    /// <summary>Gets a continue result.</summary>
    public static PluginBeforeCompactionResult Continue { get; } = new();

    /// <summary>Gets a value indicating whether compaction should be cancelled.</summary>
    public bool Cancel { get; init; }

    /// <summary>Gets the cancellation reason.</summary>
    public string? Reason { get; init; }

    /// <summary>Creates a cancellation result.</summary>
    /// <param name="reason">The cancellation reason.</param>
    /// <returns>The result.</returns>
    public static PluginBeforeCompactionResult CancelWithReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PluginBeforeCompactionResult { Cancel = true, Reason = reason };
    }
}

/// <summary>Describes compaction instructions supplied by a plugin.</summary>
public sealed record PluginCompactionInstructionResult
{
    /// <summary>Gets an empty instruction result.</summary>
    public static PluginCompactionInstructionResult Empty { get; } = new();

    /// <summary>Gets instruction text.</summary>
    public string? Instructions { get; init; }

    /// <summary>Gets an optional title for diagnostics.</summary>
    public string? Title { get; init; }
}

/// <summary>Describes the result of reducing plugin-owned compaction payloads.</summary>
public sealed record PluginCompactionReducerResult
{
    /// <summary>Gets a not-handled result.</summary>
    public static PluginCompactionReducerResult NotHandled { get; } = new() { Handled = false };

    /// <summary>Gets a value indicating whether the payload was handled.</summary>
    public bool Handled { get; init; }

    /// <summary>Gets compacted text for the payload.</summary>
    public string? CompactedText { get; init; }

    /// <summary>Gets reduced structured payload data.</summary>
    public object? ReducedPayload { get; init; }

    /// <summary>Creates a text reduction result.</summary>
    /// <param name="text">The compacted text.</param>
    /// <returns>The result.</returns>
    public static PluginCompactionReducerResult FromText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new PluginCompactionReducerResult { Handled = true, CompactedText = text };
    }
}
