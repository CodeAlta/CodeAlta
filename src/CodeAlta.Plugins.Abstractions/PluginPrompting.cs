using CodeAlta.Agent;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>Describes a prompt processor contribution.</summary>
public sealed record PluginPromptProcessorContribution
{
    /// <summary>Gets the processing order.</summary>
    public int Order { get; init; }

    /// <summary>Gets the processor handler.</summary>
    public required PluginPromptProcessorHandler Handler { get; init; }
}

/// <summary>Delegate for attaching a plugin-owned behavior to a prompt editor.</summary>
/// <param name="host">The prompt editor host.</param>
/// <returns>A disposable attachment, or <see langword="null" /> when the plugin declines to attach.</returns>
public delegate IAsyncDisposable? PluginPromptEditorAttachHandler(IPluginPromptEditorHost host);

/// <summary>Describes a plugin-owned prompt editor attachment.</summary>
public sealed record PluginPromptEditorContribution
{
    /// <summary>Gets the contribution name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets optional ready-prompt placeholder guidance shown while the contribution applies.
    /// </summary>
    /// <remarks>
    /// Keep this short and phrase it as a single placeholder segment, for example
    /// <c>[#] to reference a GitHub issue</c>.
    /// </remarks>
    public string? PlaceholderText { get; init; }

    /// <summary>Gets the ordering hint.</summary>
    public int Order { get; init; }

    /// <summary>Gets the attach handler.</summary>
    public required PluginPromptEditorAttachHandler Attach { get; init; }
}

/// <summary>Searches the items of a prompt picker.</summary>
/// <param name="context">The search context.</param>
/// <param name="cancellationToken">A token cancelled when the query changes or the picker closes.</param>
/// <returns>The matching items, best first.</returns>
public delegate ValueTask<IReadOnlyList<PluginPromptPickerItem>> PluginPromptPickerSearchHandler(PluginPromptPickerContext context, CancellationToken cancellationToken);

/// <summary>
/// Describes a picker that opens when the user types a trigger character in the prompt, and inserts the
/// text of the chosen item. The frontend supplies the picker; the plugin supplies the items.
/// </summary>
public sealed record PluginPromptPickerContribution
{
    /// <summary>Gets the contribution name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the character that opens the picker when typed at the start of a word, such as <c>!</c>.</summary>
    /// <remarks><c>@</c>, <c>#</c> and <c>/</c> are used by CodeAlta.</remarks>
    public required char Trigger { get; init; }

    /// <summary>Gets the picker title.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets optional ready-prompt placeholder guidance, for example <c>[!] to reference a ticket</c>.
    /// </summary>
    public string? PlaceholderText { get; init; }

    /// <summary>Gets the ordering hint.</summary>
    public int Order { get; init; }

    /// <summary>Gets the search handler.</summary>
    public required PluginPromptPickerSearchHandler SearchAsync { get; init; }
}

/// <summary>Context for a prompt picker search.</summary>
public sealed class PluginPromptPickerContext : PluginOperationContext
{
    /// <summary>Gets the text typed after the trigger character; empty when the picker has just opened.</summary>
    public required string Query { get; init; }
}

/// <summary>Describes an item of a prompt picker.</summary>
public sealed record PluginPromptPickerItem
{
    /// <summary>Gets the item label.</summary>
    public required string Label { get; init; }

    /// <summary>Gets an optional second column, such as a state or a path.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the text inserted in the prompt in place of the trigger and the query.</summary>
    public required string InsertText { get; init; }
}

/// <summary>Minimal host surface exposed to plugin-owned prompt editor attachments.</summary>
public interface IPluginPromptEditorHost
{
    /// <summary>Raised after prompt editor text, caret, or selection state may have changed.</summary>
    event EventHandler? EditorStateChanged;

    /// <summary>Raised when the prompt editor is accepted.</summary>
    event EventHandler? Accepted;

    /// <summary>Gets the prompt editor project path, when known.</summary>
    string? ProjectPath { get; }

    /// <summary>Gets or sets the prompt text.</summary>
    string? Text { get; set; }

    /// <summary>Gets or sets the caret index.</summary>
    int CaretIndex { get; set; }

    /// <summary>Returns focus to the prompt editor.</summary>
    void FocusPromptEditor();
}

/// <summary>Describes a prompt-processing result.</summary>
public sealed record PluginPromptResult
{
    /// <summary>Gets a continue result.</summary>
    public static PluginPromptResult Continue { get; } = new() { Disposition = PluginPromptDisposition.Continue };

    /// <summary>Gets the prompt disposition.</summary>
    public PluginPromptDisposition Disposition { get; init; }

    /// <summary>Gets replacement prompt text.</summary>
    public string? ReplacementText { get; init; }

    /// <summary>Gets replacement attachments.</summary>
    public IReadOnlyList<PluginPromptAttachment> ReplacementAttachments { get; init; } = [];

    /// <summary>Gets an optional user-visible message.</summary>
    public string? UserMessage { get; init; }

    /// <summary>Gets temporary prompt contributions for this turn.</summary>
    public IReadOnlyList<PluginSystemPromptContribution> TemporaryPromptContributions { get; init; } = [];

    /// <summary>Creates a replacement prompt result.</summary>
    /// <param name="text">The replacement prompt text.</param>
    /// <param name="attachments">Optional replacement attachments.</param>
    /// <returns>The prompt result.</returns>
    public static PluginPromptResult Replace(string text, IReadOnlyList<PluginPromptAttachment>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new PluginPromptResult
        {
            Disposition = PluginPromptDisposition.Replace,
            ReplacementText = text,
            ReplacementAttachments = attachments ?? [],
        };
    }

    /// <summary>Creates a handled prompt result.</summary>
    /// <param name="message">An optional user-visible message.</param>
    /// <returns>The prompt result.</returns>
    public static PluginPromptResult Handled(string? message = null) => new() { Disposition = PluginPromptDisposition.Handled, UserMessage = message };

    /// <summary>Creates a cancelled prompt result.</summary>
    /// <param name="message">An optional user-visible cancellation message.</param>
    /// <returns>The prompt result.</returns>
    public static PluginPromptResult Cancel(string? message = null) => new() { Disposition = PluginPromptDisposition.Cancel, UserMessage = message };
}

/// <summary>Identifies prompt processing disposition.</summary>
public enum PluginPromptDisposition
{
    /// <summary>Continue normal prompt processing.</summary>
    Continue,
    /// <summary>Replace prompt text or attachments and continue.</summary>
    Replace,
    /// <summary>Mark the prompt handled without sending to a model.</summary>
    Handled,
    /// <summary>Cancel prompt submission.</summary>
    Cancel,
}

/// <summary>Describes a final system/developer instruction processor contribution.</summary>
public sealed record PluginInstructionProcessorContribution
{
    /// <summary>Gets the natural contribution name used for diagnostics and audit metadata.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the processing order.</summary>
    public int Order { get; init; }

    /// <summary>Gets the declared processing target.</summary>
    public PluginInstructionProcessingTarget Target { get; init; } = PluginInstructionProcessingTarget.Default;

    /// <summary>Gets declared processor capabilities for UI and audit display.</summary>
    public PluginInstructionProcessorCapabilities Capabilities { get; init; } = PluginInstructionProcessorCapabilities.Read | PluginInstructionProcessorCapabilities.Replace;

    /// <summary>Gets the processor handler.</summary>
    public required PluginInstructionProcessorHandler Handler { get; init; }
}

/// <summary>Describes applicability for a final instruction processor.</summary>
public sealed record PluginInstructionProcessingTarget
{
    /// <summary>Gets the default processing target.</summary>
    public static PluginInstructionProcessingTarget Default { get; } = new();

    /// <summary>Gets the instruction channels the processor is interested in.</summary>
    public PluginInstructionChannels Channels { get; init; } = PluginInstructionChannels.All;

    /// <summary>Gets the processing stages the processor applies to.</summary>
    public PluginInstructionProcessingStages Stages { get; init; } = PluginInstructionProcessingStages.FinalBeforeProviderRequest;

    /// <summary>Gets provider identifiers this processor applies to. Empty means all providers.</summary>
    public IReadOnlyList<string> ProviderIds { get; init; } = [];

    /// <summary>Gets provider family identifiers this processor applies to. Empty means all provider families.</summary>
    public IReadOnlyList<string> ProviderFamilies { get; init; } = [];

    /// <summary>Gets model identifiers this processor applies to. Empty means all models.</summary>
    public IReadOnlyList<string> ModelIds { get; init; } = [];

    /// <summary>Gets a value indicating whether the processor requires a CodeAlta-managed provider.</summary>
    public bool RequiresCodeAltaManagedProvider { get; init; }
}

/// <summary>Identifies instruction channels available to processors.</summary>
[Flags]
public enum PluginInstructionChannels
{
    /// <summary>No instruction channels.</summary>
    None = 0,
    /// <summary>The system message channel.</summary>
    System = 1 << 0,
    /// <summary>The developer instructions channel.</summary>
    Developer = 1 << 1,
    /// <summary>All instruction channels.</summary>
    All = System | Developer,
}

/// <summary>Identifies final instruction processing stages.</summary>
[Flags]
public enum PluginInstructionProcessingStages
{
    /// <summary>No processing stages.</summary>
    None = 0,
    /// <summary>Instructions are fully composed and about to be used for provider requests and prompt audit events.</summary>
    FinalBeforeProviderRequest = 1 << 0,
}

/// <summary>Identifies the purpose for instruction processing.</summary>
public enum PluginInstructionProcessingPurpose
{
    /// <summary>An ordinary agent run.</summary>
    AgentRun,
    /// <summary>A compaction run.</summary>
    Compaction,
    /// <summary>A summary or summarization run.</summary>
    Summary,
    /// <summary>A recovery run for provider/tool overflow handling.</summary>
    ToolResultOverflowRecovery,
}

/// <summary>Declares instruction processor capabilities.</summary>
[Flags]
public enum PluginInstructionProcessorCapabilities
{
    /// <summary>No declared capabilities.</summary>
    None = 0,
    /// <summary>The processor reads final instructions.</summary>
    Read = 1 << 0,
    /// <summary>The processor may replace one or more instruction channels.</summary>
    Replace = 1 << 1,
    /// <summary>The processor may cancel the run.</summary>
    Cancel = 1 << 2,
    /// <summary>The processor reports an audit-safe change summary.</summary>
    ReportsChangeSummary = 1 << 3,
}

/// <summary>Context for final system/developer instruction processing.</summary>
public sealed class PluginInstructionProcessingContext : PluginOperationContext
{
    /// <summary>Gets the processing stage.</summary>
    public PluginInstructionProcessingStages Stage { get; init; } = PluginInstructionProcessingStages.FinalBeforeProviderRequest;

    /// <summary>Gets the processing purpose.</summary>
    public PluginInstructionProcessingPurpose Purpose { get; init; } = PluginInstructionProcessingPurpose.AgentRun;

    /// <summary>Gets the current instruction snapshot visible to this processor.</summary>
    public required PluginInstructionSnapshot Instructions { get; init; }

    /// <summary>Gets compact manifest metadata for the composed prompt, when available.</summary>
    public PluginInstructionManifestView? Manifest { get; init; }

    /// <summary>Gets prompt part descriptors for audit and targeting. Part text is intentionally omitted.</summary>
    public IReadOnlyList<PluginInstructionPartDescriptor> Parts { get; init; } = [];

    /// <summary>Gets active tool names visible for the run.</summary>
    public IReadOnlyList<string> ActiveToolNames { get; init; } = [];

    /// <summary>Gets prior instruction transformations in this processing chain.</summary>
    public IReadOnlyList<PluginInstructionTransformationRecord> PriorTransformations { get; init; } = [];

    /// <summary>Gets host-supplied extension metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Immutable snapshot of final instruction text seen by a processor.</summary>
public sealed record PluginInstructionSnapshot
{
    /// <summary>Gets the system message.</summary>
    public string? SystemMessage { get; init; }

    /// <summary>Gets the developer instructions.</summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>Gets the channel mapping label.</summary>
    public string ChannelMapping { get; init; } = "native-system-and-developer";

    /// <summary>Gets the hash for the current instruction text.</summary>
    public required string InstructionHash { get; init; }

    /// <summary>Gets the system-message character count.</summary>
    public int SystemCharacterCount { get; init; }

    /// <summary>Gets the developer-instruction character count.</summary>
    public int DeveloperCharacterCount { get; init; }

    /// <summary>Gets the approximate system-message token count.</summary>
    public int SystemApproxTokens { get; init; }

    /// <summary>Gets the approximate developer-instruction token count.</summary>
    public int DeveloperApproxTokens { get; init; }
}

/// <summary>Compact read-only prompt manifest metadata exposed to instruction processors.</summary>
public sealed record PluginInstructionManifestView
{
    /// <summary>Gets the prompt manifest version.</summary>
    public int Version { get; init; }

    /// <summary>Gets the prompt identifier, when known.</summary>
    public string? PromptId { get; init; }

    /// <summary>Gets the effective prompt hash before instruction processing, when known.</summary>
    public string? EffectivePromptHash { get; init; }

    /// <summary>Gets the selected system prompt name, when known.</summary>
    public string? SystemPromptName { get; init; }

    /// <summary>Gets the selected agent prompt name, when known.</summary>
    public string? AgentPromptName { get; init; }

    /// <summary>Gets a value indicating whether generated skills context was enabled.</summary>
    public bool SkillsEnabled { get; init; }

    /// <summary>Gets a value indicating whether generated project context was enabled.</summary>
    public bool ProjectContextEnabled { get; init; }

    /// <summary>Gets a value indicating whether generated runtime context was enabled.</summary>
    public bool RuntimeContextEnabled { get; init; }

    /// <summary>Gets a value indicating whether generated tool guidance was enabled.</summary>
    public bool ToolGuidanceEnabled { get; init; }

    /// <summary>Gets the prompt diagnostic count.</summary>
    public int DiagnosticCount { get; init; }
}

/// <summary>Prompt part descriptor exposed to instruction processors without part text.</summary>
public sealed record PluginInstructionPartDescriptor
{
    /// <summary>Gets the stable part key.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the part kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the logical part name, when known.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the target channel label.</summary>
    public required string Target { get; init; }

    /// <summary>Gets the part source kind.</summary>
    public required string SourceKind { get; init; }

    /// <summary>Gets the source path, when available.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Gets the part order.</summary>
    public int Order { get; init; }

    /// <summary>Gets the part status.</summary>
    public string? Status { get; init; }
}

/// <summary>Describes a final instruction processing result.</summary>
public sealed record PluginInstructionProcessingResult
{
    /// <summary>Gets a continue result.</summary>
    public static PluginInstructionProcessingResult Continue { get; } = new() { Disposition = PluginInstructionProcessingDisposition.Continue };

    /// <summary>Gets the result disposition.</summary>
    public PluginInstructionProcessingDisposition Disposition { get; init; }

    /// <summary>Gets the replacement system message. <see langword="null" /> preserves the current value.</summary>
    public string? ReplacementSystemMessage { get; init; }

    /// <summary>Gets the replacement developer instructions. <see langword="null" /> preserves the current value.</summary>
    public string? ReplacementDeveloperInstructions { get; init; }

    /// <summary>Gets an audit-safe change summary.</summary>
    public string? ChangeSummary { get; init; }

    /// <summary>Gets a user-visible message for cancellation or blocking.</summary>
    public string? UserMessage { get; init; }

    /// <summary>Gets audit-safe plugin-owned metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Creates a replacement result.</summary>
    /// <param name="systemMessage">Replacement system message, or <see langword="null" /> to preserve it.</param>
    /// <param name="developerInstructions">Replacement developer instructions, or <see langword="null" /> to preserve them.</param>
    /// <param name="changeSummary">Audit-safe change summary.</param>
    /// <returns>The instruction processing result.</returns>
    public static PluginInstructionProcessingResult Replace(string? systemMessage = null, string? developerInstructions = null, string? changeSummary = null)
        => new()
        {
            Disposition = PluginInstructionProcessingDisposition.Replace,
            ReplacementSystemMessage = systemMessage,
            ReplacementDeveloperInstructions = developerInstructions,
            ChangeSummary = changeSummary,
        };

    /// <summary>Creates a cancellation result.</summary>
    /// <param name="message">User-visible cancellation reason.</param>
    /// <returns>The instruction processing result.</returns>
    public static PluginInstructionProcessingResult Cancel(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new PluginInstructionProcessingResult { Disposition = PluginInstructionProcessingDisposition.Cancel, UserMessage = message };
    }
}

/// <summary>Identifies a final instruction processing disposition.</summary>
public enum PluginInstructionProcessingDisposition
{
    /// <summary>Continue without modifying instructions.</summary>
    Continue,
    /// <summary>Replace one or more instruction channels.</summary>
    Replace,
    /// <summary>Cancel the run before provider submission.</summary>
    Cancel,
}

/// <summary>Audit metadata for one final instruction transformation.</summary>
public sealed record PluginInstructionTransformationRecord
{
    /// <summary>Gets the plugin runtime key.</summary>
    public required string PluginRuntimeKey { get; init; }

    /// <summary>Gets the runtime contribution key.</summary>
    public required string RuntimeContributionKey { get; init; }

    /// <summary>Gets the natural contribution name, when known.</summary>
    public string? NaturalName { get; init; }

    /// <summary>Gets the processor order.</summary>
    public int Order { get; init; }

    /// <summary>Gets the processing stage.</summary>
    public PluginInstructionProcessingStages Stage { get; init; }

    /// <summary>Gets the result disposition.</summary>
    public PluginInstructionProcessingDisposition Disposition { get; init; }

    /// <summary>Gets changed instruction channels.</summary>
    public IReadOnlyList<string> ChangedChannels { get; init; } = [];

    /// <summary>Gets an audit-safe change summary.</summary>
    public string? ChangeSummary { get; init; }

    /// <summary>Gets the post-transform instruction hash.</summary>
    public string? ResultInstructionHash { get; init; }

    /// <summary>Gets audit-safe plugin-owned metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Describes a prompt attachment.</summary>
public sealed record PluginPromptAttachment
{
    /// <summary>Gets the attachment kind.</summary>
    public required PluginPromptAttachmentKind Kind { get; init; }

    /// <summary>Gets the attachment path, when path-backed.</summary>
    public string? Path { get; init; }

    /// <summary>Gets display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets text content, when text-backed.</summary>
    public string? Text { get; init; }

    /// <summary>Gets media type, when known.</summary>
    public string? MediaType { get; init; }

    /// <summary>Gets plugin-specific metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Identifies a prompt attachment kind.</summary>
public enum PluginPromptAttachmentKind
{
    /// <summary>Text content.</summary>
    Text,
    /// <summary>A file path.</summary>
    File,
    /// <summary>A directory path.</summary>
    Directory,
    /// <summary>An image path or URL.</summary>
    Image,
    /// <summary>A selection from a file or editor.</summary>
    Selection,
    /// <summary>Plugin-specific metadata.</summary>
    Metadata,
}

/// <summary>Describes a system/developer prompt contribution.</summary>
public sealed record PluginSystemPromptContribution
{
    /// <summary>Gets an optional prompt part title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the target prompt channel.</summary>
    public required PluginPromptChannel Channel { get; init; }

    /// <summary>Gets the prompt part kind.</summary>
    public PluginPromptPartKind Kind { get; init; } = PluginPromptPartKind.Guidance;

    /// <summary>Gets the ordering hint.</summary>
    public int Order { get; init; }

    /// <summary>Gets the content provider.</summary>
    public required PluginSystemPromptContentProvider Content { get; init; }

    /// <summary>Gets the source path, when known.</summary>
    public string? SourcePath { get; init; }
}

/// <summary>Identifies a prompt channel.</summary>
public enum PluginPromptChannel
{
    /// <summary>System prompt channel.</summary>
    System,
    /// <summary>Developer prompt channel.</summary>
    Developer,
}

/// <summary>Identifies a prompt part kind.</summary>
public enum PluginPromptPartKind
{
    /// <summary>Policy content.</summary>
    Policy,
    /// <summary>Guidance content.</summary>
    Guidance,
    /// <summary>Tool usage guidance.</summary>
    ToolGuidance,
    /// <summary>Runtime context.</summary>
    RuntimeContext,
    /// <summary>Project context.</summary>
    ProjectContext,
    /// <summary>Skill advertisement.</summary>
    SkillAdvertisement,
    /// <summary>Other content.</summary>
    Other,
}

/// <summary>Describes a dynamic prompt message supplied before an agent run.</summary>
public sealed record PluginPromptMessage
{
    /// <summary>Gets the message role.</summary>
    public required PluginPromptMessageRole Role { get; init; }

    /// <summary>Gets the message content.</summary>
    public required string Content { get; init; }

    /// <summary>Gets optional metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Identifies a prompt message role.</summary>
public enum PluginPromptMessageRole
{
    /// <summary>System role.</summary>
    System,
    /// <summary>Developer role.</summary>
    Developer,
    /// <summary>User role.</summary>
    User,
    /// <summary>Assistant role.</summary>
    Assistant,
}

/// <summary>Describes before-agent-run effects.</summary>
public sealed record PluginBeforeAgentRunResult
{
    /// <summary>Gets an empty result.</summary>
    public static PluginBeforeAgentRunResult Empty { get; } = new();

    /// <summary>Gets a value indicating whether to cancel the run.</summary>
    public bool Cancel { get; init; }

    /// <summary>Gets a user-visible cancellation reason.</summary>
    public string? CancelReason { get; init; }

    /// <summary>Gets additional prompt messages.</summary>
    public IReadOnlyList<PluginPromptMessage> AdditionalMessages { get; init; } = [];

    /// <summary>Gets temporary prompt contributions.</summary>
    public IReadOnlyList<PluginSystemPromptContribution> TemporaryPromptContributions { get; init; } = [];

    /// <summary>Gets tool names that should be preferred for this run.</summary>
    public IReadOnlyList<string> PreferredToolNames { get; init; } = [];

    /// <summary>Gets additional agent tools that should be available for this run.</summary>
    public IReadOnlyList<AgentToolDefinition> AdditionalTools { get; init; } = [];

    /// <summary>Gets an optional model hint.</summary>
    public string? ModelHint { get; init; }

    /// <summary>Creates a cancellation result.</summary>
    /// <param name="reason">The cancellation reason.</param>
    /// <returns>The before-run result.</returns>
    public static PluginBeforeAgentRunResult CancelRun(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PluginBeforeAgentRunResult { Cancel = true, CancelReason = reason };
    }
}
