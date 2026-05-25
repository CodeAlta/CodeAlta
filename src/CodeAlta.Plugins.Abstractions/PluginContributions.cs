using System.Text.Json;
using CodeAlta.Agent;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Identifies a plugin contribution point.
/// </summary>
public enum PluginPoint
{
    /// <summary>Startup hook.</summary>
    Startup,
    /// <summary>Command-line option.</summary>
    CommandLine,
    /// <summary>Command or shortcut.</summary>
    Command,
    /// <summary>Agent tool.</summary>
    AgentTool,
    /// <summary>Agent backend/provider.</summary>
    AgentBackend,
    /// <summary>Alta live command.</summary>
    AltaCommand,
    /// <summary>System/developer prompt part.</summary>
    SystemPrompt,
    /// <summary>Prompt processor.</summary>
    PromptProcessor,
    /// <summary>Prompt editor attachment.</summary>
    PromptEditor,
    /// <summary>Before-agent-run hook.</summary>
    BeforeAgentRun,
    /// <summary>Tool-call hook.</summary>
    ToolCall,
    /// <summary>Tool-result hook.</summary>
    ToolResult,
    /// <summary>Agent event observer.</summary>
    AgentEvent,
    /// <summary>Plugin-derived transient thread event projector.</summary>
    ThreadEventProjection,
    /// <summary>Compaction hook.</summary>
    Compaction,
    /// <summary>UI contribution.</summary>
    Ui,
    /// <summary>Resource contribution.</summary>
    Resource,
}

/// <summary>
/// Runtime-owned handle for a materialized contribution.
/// </summary>
public sealed record PluginContributionHandle
{
    /// <summary>Gets the plugin runtime key.</summary>
    public required string PluginRuntimeKey { get; init; }

    /// <summary>Gets the plugin type name.</summary>
    public required string PluginTypeName { get; init; }

    /// <summary>Gets the contribution point.</summary>
    public required PluginPoint Point { get; init; }

    /// <summary>Gets the runtime contribution key.</summary>
    public required string RuntimeContributionKey { get; init; }

    /// <summary>Gets the contribution natural name, when available.</summary>
    public string? NaturalName { get; init; }

    /// <summary>Gets the ordinal within the plugin contribution list.</summary>
    public int Ordinal { get; init; }

    /// <summary>Gets the plugin activation generation.</summary>
    public int ActivationGeneration { get; init; }

    /// <summary>
    /// Creates a deterministic runtime-owned handle.
    /// </summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <param name="pluginTypeName">The plugin type name.</param>
    /// <param name="point">The contribution point.</param>
    /// <param name="naturalName">The natural contribution name, when available.</param>
    /// <param name="ordinal">The contribution ordinal.</param>
    /// <param name="activationGeneration">The activation generation.</param>
    /// <returns>The contribution handle.</returns>
    public static PluginContributionHandle Create(
        string pluginRuntimeKey,
        string pluginTypeName,
        PluginPoint point,
        string? naturalName,
        int ordinal,
        int activationGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginTypeName);
        var name = string.IsNullOrWhiteSpace(naturalName) ? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) : naturalName.Trim();
        return new PluginContributionHandle
        {
            PluginRuntimeKey = pluginRuntimeKey,
            PluginTypeName = pluginTypeName,
            Point = point,
            NaturalName = string.IsNullOrWhiteSpace(naturalName) ? null : naturalName.Trim(),
            Ordinal = ordinal,
            ActivationGeneration = activationGeneration,
            RuntimeContributionKey = $"{pluginRuntimeKey}:{pluginTypeName}:{point}:{name}:{ordinal}:{activationGeneration}",
        };
    }
}

/// <summary>
/// Common metadata for plugin contributions.
/// </summary>
public sealed record PluginContributionMetadata
{
    /// <summary>Gets the natural contribution name, when available.</summary>
    public string? NaturalName { get; init; }

    /// <summary>Gets the label shown in UI.</summary>
    public string? Label { get; init; }

    /// <summary>Gets the description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the explicit ordering hint.</summary>
    public int Order { get; init; }

    /// <summary>Gets the source path, when available.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Gets additional metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Delegate for early startup contributions.</summary>
/// <param name="context">The startup context.</param>
/// <param name="cancellationToken">A token to cancel the operation.</param>
/// <returns>A task representing asynchronous startup work.</returns>
public delegate ValueTask PluginStartupHandler(PluginStartupContext context, CancellationToken cancellationToken);

/// <summary>Delegate for command handlers.</summary>
/// <param name="context">The command context.</param>
/// <param name="cancellationToken">A token to cancel the operation.</param>
/// <returns>The command result.</returns>
public delegate ValueTask<PluginCommandResult> PluginCommandHandler(PluginCommandContext context, CancellationToken cancellationToken);

/// <summary>Delegate for prompt processors.</summary>
/// <param name="context">The prompt submission context.</param>
/// <param name="cancellationToken">A token to cancel the operation.</param>
/// <returns>The prompt result.</returns>
public delegate ValueTask<PluginPromptResult> PluginPromptProcessorHandler(PluginPromptSubmittingContext context, CancellationToken cancellationToken);

/// <summary>Delegate for system prompt content providers.</summary>
/// <param name="context">The prompt context.</param>
/// <param name="cancellationToken">A token to cancel the operation.</param>
/// <returns>The prompt content, or <see langword="null"/> for no content.</returns>
public delegate ValueTask<string?> PluginSystemPromptContentProvider(PluginSystemPromptContext context, CancellationToken cancellationToken);

/// <summary>Delegate for tool renderers.</summary>
/// <param name="context">The renderer context.</param>
/// <param name="cancellationToken">A token to cancel rendering.</param>
/// <returns>A rendered visual or markdown fallback.</returns>
public delegate ValueTask<PluginRenderResult?> PluginRenderer(PluginRendererContext context, CancellationToken cancellationToken);

/// <summary>Delegate for backend factory callbacks.</summary>
/// <param name="context">The backend factory context.</param>
/// <param name="cancellationToken">A token to cancel backend creation.</param>
/// <returns>The backend instance.</returns>
public delegate ValueTask<IAgentBackend> PluginAgentBackendFactory(PluginAgentBackendFactoryContext context, CancellationToken cancellationToken);

/// <summary>Delegate for before-compaction hooks.</summary>
/// <param name="context">The compaction context.</param>
/// <param name="cancellationToken">A token to cancel the hook.</param>
/// <returns>The hook result.</returns>
public delegate ValueTask<PluginBeforeCompactionResult> PluginBeforeCompactionHandler(PluginBeforeCompactionContext context, CancellationToken cancellationToken);

/// <summary>Delegate for compaction instruction providers.</summary>
/// <param name="context">The instruction context.</param>
/// <param name="cancellationToken">A token to cancel the provider.</param>
/// <returns>The instruction result.</returns>
public delegate ValueTask<PluginCompactionInstructionResult> PluginCompactionInstructionProvider(PluginCompactionInstructionContext context, CancellationToken cancellationToken);

/// <summary>Delegate for compaction reducers.</summary>
/// <param name="context">The reducer context.</param>
/// <param name="cancellationToken">A token to cancel reduction.</param>
/// <returns>The reduction result.</returns>
public delegate ValueTask<PluginCompactionReducerResult> PluginCompactionReducer(PluginCompactionReducerContext context, CancellationToken cancellationToken);

/// <summary>Delegate for after-compaction hooks.</summary>
/// <param name="context">The compaction context.</param>
/// <param name="cancellationToken">A token to cancel the hook.</param>
/// <returns>A task representing asynchronous hook work.</returns>
public delegate ValueTask PluginAfterCompactionHandler(PluginAfterCompactionContext context, CancellationToken cancellationToken);

/// <summary>Represents an early startup contribution.</summary>
public sealed record PluginStartupContribution
{
    /// <summary>Gets the natural name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the ordering hint.</summary>
    public int Order { get; init; }

    /// <summary>Gets the startup handler.</summary>
    public PluginStartupHandler? Handler { get; init; }

    /// <summary>Gets early resource contributions.</summary>
    public IReadOnlyList<PluginResourceContribution> Resources { get; init; } = [];
}

/// <summary>Describes a command or shortcut contribution.</summary>
public sealed record PluginCommandContribution
{
    /// <summary>Gets the command natural name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the command label.</summary>
    public string? Label { get; init; }

    /// <summary>Gets the command description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the command kind.</summary>
    public PluginCommandKind Kind { get; init; }

    /// <summary>Gets command aliases.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Gets an optional key binding.</summary>
    public PluginKeyBinding? KeyBinding { get; init; }

    /// <summary>Gets command presentation metadata.</summary>
    public PluginCommandPresentation Presentation { get; init; } = PluginCommandPresentation.Default;

    /// <summary>Gets command availability metadata.</summary>
    public PluginCommandAvailability Availability { get; init; } = PluginCommandAvailability.Always;

    /// <summary>Gets the command handler.</summary>
    public required PluginCommandHandler Handler { get; init; }

    /// <summary>Gets the ordering hint.</summary>
    public int Order { get; init; }
}

/// <summary>Identifies a plugin command kind.</summary>
public enum PluginCommandKind
{
    /// <summary>A global shell command.</summary>
    Shell,
    /// <summary>A prompt slash/text command.</summary>
    Prompt,
    /// <summary>A selected-thread command.</summary>
    Thread,
}

/// <summary>Describes a plugin key binding.</summary>
public sealed record PluginKeyBinding
{
    /// <summary>Gets display text for the binding.</summary>
    public required string DisplayText { get; init; }

    /// <summary>Gets a single-key gesture.</summary>
    public KeyGesture? Gesture { get; init; }

    /// <summary>Gets a multi-key sequence.</summary>
    public KeySequence? Sequence { get; init; }
}

/// <summary>Describes plugin command presentation.</summary>
public sealed record PluginCommandPresentation
{
    /// <summary>Gets the default presentation.</summary>
    public static PluginCommandPresentation Default { get; } = new();

    /// <summary>Gets a value indicating whether to show in the command palette.</summary>
    public bool ShowInCommandPalette { get; init; } = true;

    /// <summary>Gets a value indicating whether to show in the command bar.</summary>
    public bool ShowInCommandBar { get; init; } = true;

    /// <summary>Gets a value indicating whether to show in help.</summary>
    public bool ShowInHelp { get; init; } = true;

    /// <summary>Gets an optional grouping label.</summary>
    public string? Group { get; init; }

    /// <summary>Gets optional icon or markup text.</summary>
    public string? IconMarkup { get; init; }
}

/// <summary>Describes when a command is available.</summary>
public sealed record PluginCommandAvailability
{
    /// <summary>Gets the always-available predicate.</summary>
    public static PluginCommandAvailability Always { get; } = new();

    /// <summary>Gets a predicate requiring interactive UI.</summary>
    public static PluginCommandAvailability InteractiveUi { get; } = new() { RequiresInteractiveUi = true };

    /// <summary>Gets a predicate requiring a project.</summary>
    public static PluginCommandAvailability ProjectSelected { get; } = new() { RequiresProject = true };

    /// <summary>Gets a predicate requiring a thread.</summary>
    public static PluginCommandAvailability ThreadSelected { get; } = new() { RequiresThread = true };

    /// <summary>Gets a value indicating whether interactive UI is required.</summary>
    public bool RequiresInteractiveUi { get; init; }

    /// <summary>Gets a value indicating whether a project is required.</summary>
    public bool RequiresProject { get; init; }

    /// <summary>Gets a value indicating whether a thread is required.</summary>
    public bool RequiresThread { get; init; }

    /// <summary>Gets a value indicating whether an idle thread is required.</summary>
    public bool RequiresIdleThread { get; init; }

    /// <summary>Gets a value indicating whether a busy thread is required.</summary>
    public bool RequiresBusyThread { get; init; }

    /// <summary>Gets a value indicating whether a CodeAlta-managed backend is required.</summary>
    public bool RequiresCodeAltaManagedBackend { get; init; }

    /// <summary>Gets allowed backend families.</summary>
    public IReadOnlyList<string> BackendFamilies { get; init; } = [];
}

/// <summary>Describes a command result.</summary>
public sealed record PluginCommandResult
{
    /// <summary>Gets a handled result.</summary>
    public static PluginCommandResult Handled { get; } = new() { Disposition = PluginCommandDisposition.Handled };

    /// <summary>Gets a not-handled result.</summary>
    public static PluginCommandResult NotHandled { get; } = new() { Disposition = PluginCommandDisposition.NotHandled };

    /// <summary>Gets a cancelled result.</summary>
    public static PluginCommandResult Cancelled { get; } = new() { Disposition = PluginCommandDisposition.Cancelled };

    /// <summary>Gets the command disposition.</summary>
    public PluginCommandDisposition Disposition { get; init; }

    /// <summary>Gets an optional user-visible message.</summary>
    public string? UserMessage { get; init; }

    /// <summary>Gets an optional prompt to send or enqueue.</summary>
    public string? PromptText { get; init; }

    /// <summary>Gets a value indicating whether <see cref="PromptText"/> should be enqueued instead of sent immediately.</summary>
    public bool EnqueuePrompt { get; init; }

    /// <summary>Creates a message result.</summary>
    /// <param name="message">The message to show.</param>
    /// <returns>The command result.</returns>
    public static PluginCommandResult Message(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new PluginCommandResult { Disposition = PluginCommandDisposition.Handled, UserMessage = message };
    }
}

/// <summary>Identifies the command result disposition.</summary>
public enum PluginCommandDisposition
{
    /// <summary>The command was handled.</summary>
    Handled,
    /// <summary>The command was not handled.</summary>
    NotHandled,
    /// <summary>The command was cancelled.</summary>
    Cancelled,
}

/// <summary>Describes a plugin-contributed agent tool.</summary>
public sealed record PluginAgentToolContribution
{
    /// <summary>Gets the tool definition.</summary>
    public required AgentToolDefinition Definition { get; init; }

    /// <summary>Gets an optional prompt snippet advertising the tool.</summary>
    public string? PromptSnippet { get; init; }

    /// <summary>Gets optional prompt guidance.</summary>
    public string? PromptGuidance { get; init; }

    /// <summary>Gets the activation policy.</summary>
    public PluginToolActivationPolicy ActivationPolicy { get; init; } = PluginToolActivationPolicy.Default;

    /// <summary>Gets an optional renderer.</summary>
    public PluginRenderer? Renderer { get; init; }
}

/// <summary>Describes tool activation policy.</summary>
public sealed record PluginToolActivationPolicy
{
    /// <summary>Gets the default always-active policy.</summary>
    public static PluginToolActivationPolicy Default { get; } = new();

    /// <summary>Gets a policy for CodeAlta-managed backends only.</summary>
    public static PluginToolActivationPolicy CodeAltaManagedOnly { get; } = new() { RequiresCodeAltaManagedBackend = true };

    /// <summary>Gets a value indicating whether a CodeAlta-managed backend is required.</summary>
    public bool RequiresCodeAltaManagedBackend { get; init; }

    /// <summary>Gets allowed backend families.</summary>
    public IReadOnlyList<string> BackendFamilies { get; init; } = [];

    /// <summary>Gets allowed backend names.</summary>
    public IReadOnlyList<string> BackendNames { get; init; } = [];
}

/// <summary>Describes the result of a tool-call hook.</summary>
public sealed record PluginToolCallResult
{
    /// <summary>Gets an allow result.</summary>
    public static PluginToolCallResult Allow { get; } = new() { Disposition = PluginToolCallDisposition.Allow };

    /// <summary>Gets the hook disposition.</summary>
    public PluginToolCallDisposition Disposition { get; init; }

    /// <summary>Gets replacement arguments when <see cref="Disposition"/> is <see cref="PluginToolCallDisposition.ReplaceArguments"/>.</summary>
    public JsonElement? ReplacementArguments { get; init; }

    /// <summary>Gets a block reason when the call is blocked.</summary>
    public string? BlockReason { get; init; }

    /// <summary>Creates a replacement-arguments result.</summary>
    /// <param name="arguments">The replacement JSON arguments.</param>
    /// <returns>The hook result.</returns>
    public static PluginToolCallResult ReplaceArguments(JsonElement arguments) => new() { Disposition = PluginToolCallDisposition.ReplaceArguments, ReplacementArguments = arguments };

    /// <summary>Creates a block result.</summary>
    /// <param name="reason">The block reason.</param>
    /// <returns>The hook result.</returns>
    public static PluginToolCallResult Block(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PluginToolCallResult { Disposition = PluginToolCallDisposition.Block, BlockReason = reason };
    }
}

/// <summary>Identifies tool-call hook disposition.</summary>
public enum PluginToolCallDisposition
{
    /// <summary>Allow the call unchanged.</summary>
    Allow,
    /// <summary>Replace tool arguments.</summary>
    ReplaceArguments,
    /// <summary>Block the call.</summary>
    Block,
}

/// <summary>Describes the result of a tool-result hook.</summary>
public sealed record PluginToolResult
{
    /// <summary>Gets an unchanged result.</summary>
    public static PluginToolResult Unchanged { get; } = new() { Disposition = PluginToolResultDisposition.Unchanged };

    /// <summary>Gets the hook disposition.</summary>
    public PluginToolResultDisposition Disposition { get; init; }

    /// <summary>Gets a replacement tool result.</summary>
    public AgentToolResult? ReplacementResult { get; init; }

    /// <summary>Gets structured UI details.</summary>
    public JsonElement? Details { get; init; }

    /// <summary>Creates a replacement-result hook result.</summary>
    /// <param name="result">The replacement result.</param>
    /// <returns>The hook result.</returns>
    public static PluginToolResult Replace(AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new PluginToolResult { Disposition = PluginToolResultDisposition.Replace, ReplacementResult = result };
    }

    /// <summary>Creates a details attachment result.</summary>
    /// <param name="details">Structured details.</param>
    /// <returns>The hook result.</returns>
    public static PluginToolResult AttachDetails(JsonElement details) => new() { Disposition = PluginToolResultDisposition.AttachDetails, Details = details };
}

/// <summary>Identifies tool-result hook disposition.</summary>
public enum PluginToolResultDisposition
{
    /// <summary>Leave the result unchanged.</summary>
    Unchanged,
    /// <summary>Replace the result.</summary>
    Replace,
    /// <summary>Attach structured details.</summary>
    AttachDetails,
}

/// <summary>Describes a backend/provider contribution.</summary>
public sealed record PluginAgentBackendContribution
{
    /// <summary>Gets the backend natural name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the backend display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the backend description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets backend capabilities.</summary>
    public PluginAgentBackendCapabilities Capabilities { get; init; }

    /// <summary>Gets the backend factory.</summary>
    public required PluginAgentBackendFactory Factory { get; init; }

    /// <summary>Gets optional provider configuration metadata.</summary>
    public PluginProviderConfigurationContribution? Configuration { get; init; }
}

/// <summary>Describes backend capabilities.</summary>
[Flags]
public enum PluginAgentBackendCapabilities
{
    /// <summary>No declared capabilities.</summary>
    None = 0,
    /// <summary>Can create sessions.</summary>
    Sessions = 1 << 0,
    /// <summary>Can resume sessions.</summary>
    Resume = 1 << 1,
    /// <summary>Can list models.</summary>
    ModelListing = 1 << 2,
    /// <summary>Supports tools.</summary>
    Tools = 1 << 3,
    /// <summary>Supports image input or output.</summary>
    Images = 1 << 4,
    /// <summary>Supports steering active runs.</summary>
    Steering = 1 << 5,
    /// <summary>Supports compaction metadata or operations.</summary>
    Compaction = 1 << 6,
    /// <summary>Typical interactive backend capabilities.</summary>
    Default = Sessions | Resume | ModelListing,
}

/// <summary>Describes optional provider configuration integration metadata.</summary>
public sealed record PluginProviderConfigurationContribution
{
    /// <summary>Gets the settings display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets credential prompt descriptors.</summary>
    public IReadOnlyList<string> CredentialPrompts { get; init; } = [];

    /// <summary>Gets default configuration overlays.</summary>
    public IReadOnlyDictionary<string, string> DefaultConfiguration { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets an optional settings UI contribution.</summary>
    public PluginUiContribution? SettingsUi { get; init; }
}
