namespace CodeAlta.Plugins.Abstractions;

/// <summary>Describes a prompt processor contribution.</summary>
public sealed record PluginPromptProcessorContribution
{
    /// <summary>Gets the processing order.</summary>
    public int Order { get; init; }

    /// <summary>Gets the processor handler.</summary>
    public required PluginPromptProcessorHandler Handler { get; init; }
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
