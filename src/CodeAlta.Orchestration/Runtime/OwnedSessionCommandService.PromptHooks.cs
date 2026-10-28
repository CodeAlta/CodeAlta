using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>
    /// The code of a send that a plugin handled itself (<see cref="PluginPromptDisposition.Handled"/>): the receipt completed
    /// without a run, nothing was sent to the agent, and <see cref="OwnedSessionCommandResult.Message"/> is what the plugin said.
    /// </summary>
    public const string PluginHandledCode = "plugin_handled";

    /// <summary>
    /// The code of a send that a plugin cancelled (<see cref="PluginPromptDisposition.Cancel"/>): the receipt is cancelled,
    /// nothing was sent to the agent, and <see cref="OwnedSessionCommandResult.Message"/> is what the plugin said.
    /// </summary>
    public const string PluginCancelledCode = "plugin_cancelled";

    // The key of PluginPromptAttachment.Metadata that names an attachment, so that a plugin that replaces the prompt can
    // keep, drop or reorder the attachments it was given.
    private const string PromptAttachmentIdKey = "id";

    /// <summary>The prompt of a send once the prompt-submit hooks of the plugins saw it.</summary>
    /// <param name="Text">The text to send: the typed one, or the one a plugin replaced it with.</param>
    /// <param name="Attachments">The attachments given by reference that are sent.</param>
    /// <param name="Images">The attached files and images that are sent.</param>
    /// <param name="Contributions">The prompt parts the plugins gave this run only.</param>
    /// <param name="Settled">The result of a send a plugin handled or cancelled: nothing is sent.</param>
    private sealed record SubmittedPrompt(string Text, IReadOnlyList<AgentInputItem>? Attachments, IReadOnlyList<OwnedPromptImage>? Images,
        IReadOnlyList<PluginSystemPromptContribution> Contributions, OwnedSessionCommandResult? Settled = null);

    // Only a prompt of the user goes through the hooks. A prompt of the host (a job result, a reminder, a fallback, the
    // continuation a run-end hook asks for) was not typed, and a plugin's own continuation must not be rewritten again.
    private async Task<SubmittedPrompt> SubmitPromptAsync(SendOperation operation, SessionViewDescriptor session,
        SessionExecutionOptions options, string? spaceId)
    {
        var request = operation.Request;
        if (Plugins is null || request.Provenance is not null)
            return new(request.Text, request.Attachments, request.Images, []);

        var attachments = request.Attachments ?? [];
        var images = request.Images ?? [];
        var given = new PluginPromptAttachment[attachments.Count + images.Count];
        for (var index = 0; index < attachments.Count; index++) given[index] = Describe(attachments[index], "attachment-" + (index + 1));
        for (var index = 0; index < images.Count; index++) given[attachments.Count + index] = Describe(images[index], "image-" + (index + 1));
        var processed = await Plugins.ProcessPromptSubmittingAsync(options, request.Text, given, session.ProjectRef, session.SessionId, spaceId,
            operation.Execution.Token).ConfigureAwait(false);
        var result = processed.Result;
        switch (result.Disposition)
        {
            case PluginPromptDisposition.Handled:
                return new(request.Text, null, null, [], new(OwnedSessionCommandOutcome.Completed, Code: PluginHandledCode) { Message = MessageOf(result) });
            case PluginPromptDisposition.Cancel:
                return new(request.Text, null, null, [], new(OwnedSessionCommandOutcome.Cancelled, Code: PluginCancelledCode) { Message = MessageOf(result) });
            case PluginPromptDisposition.Replace:
                var text = result.ReplacementText ?? request.Text;
                if (!TryKeep(result.ReplacementAttachments, attachments, images, out var keptAttachments, out var keptImages))
                    (keptAttachments, keptImages) = (attachments, images);
                if (string.IsNullOrWhiteSpace(text) && keptAttachments.Count == 0 && keptImages.Count == 0)
                    throw new ArgumentException("A plugin replaced the prompt with nothing to send.");
                return new(text, keptAttachments.Count == 0 ? null : keptAttachments, keptImages.Count == 0 ? null : keptImages,
                    result.TemporaryPromptContributions);
            default:
                return new(request.Text, request.Attachments, request.Images, result.TemporaryPromptContributions);
        }
    }

    private static string? MessageOf(PluginPromptResult result)
        => string.IsNullOrWhiteSpace(result.UserMessage) ? null : result.UserMessage.Trim();

    // The attachments a plugin returned, by the identity it was given: a subset of them in any order. An empty list, or one
    // with an attachment the host did not give, keeps them all as they were.
    private static bool TryKeep(IReadOnlyList<PluginPromptAttachment> returned, IReadOnlyList<AgentInputItem> attachments,
        IReadOnlyList<OwnedPromptImage> images, out IReadOnlyList<AgentInputItem> keptAttachments, out IReadOnlyList<OwnedPromptImage> keptImages)
    {
        keptAttachments = attachments;
        keptImages = images;
        if (returned.Count == 0) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nextAttachments = new List<AgentInputItem>();
        var nextImages = new List<OwnedPromptImage>();
        foreach (var attachment in returned)
        {
            if (attachment?.Metadata is null || !attachment.Metadata.TryGetValue(PromptAttachmentIdKey, out var id) || !seen.Add(id)) return false;
            if (TryIndex(id, "attachment-", attachments.Count, out var index)) nextAttachments.Add(attachments[index]);
            else if (TryIndex(id, "image-", images.Count, out index)) nextImages.Add(images[index]);
            else return false;
        }

        keptAttachments = nextAttachments;
        keptImages = nextImages;
        return true;
    }

    private static bool TryIndex(string id, string prefix, int count, out int index)
    {
        index = -1;
        if (!id.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(id.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number < 1 || number > count) return false;
        index = number - 1;
        return true;
    }

    private static PluginPromptAttachment Describe(OwnedPromptImage image, string id) => new()
    {
        Kind = OwnedPromptImages.IsImage(image) ? PluginPromptAttachmentKind.Image : PluginPromptAttachmentKind.File,
        DisplayName = image.Title,
        MediaType = image.MediaType,
        Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [PromptAttachmentIdKey] = id },
    };

    private static PluginPromptAttachment Describe(AgentInputItem item, string id)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [PromptAttachmentIdKey] = id };
        return item switch
        {
            AgentInputItem.Text text => new() { Kind = PluginPromptAttachmentKind.Text, Text = text.Value, Metadata = metadata },
            AgentInputItem.LocalImage image => new() { Kind = PluginPromptAttachmentKind.Image, Path = image.Path, DisplayName = image.DisplayName, MediaType = image.MediaType, Metadata = metadata },
            AgentInputItem.ImageUrl image => new() { Kind = PluginPromptAttachmentKind.Image, Path = image.Url, Metadata = metadata },
            AgentInputItem.LocalFile file => new() { Kind = PluginPromptAttachmentKind.File, Path = file.Path, DisplayName = file.DisplayName, MediaType = file.MediaType, Metadata = metadata },
            AgentInputItem.File file => new() { Kind = PluginPromptAttachmentKind.File, Path = file.Path, DisplayName = file.DisplayName, Metadata = metadata },
            AgentInputItem.Directory directory => new() { Kind = PluginPromptAttachmentKind.Directory, Path = directory.Path, DisplayName = directory.DisplayName, Metadata = metadata },
            AgentInputItem.Selection selection => new() { Kind = PluginPromptAttachmentKind.Selection, Path = selection.FilePath, DisplayName = selection.DisplayName, Text = selection.SelectedText, Metadata = metadata },
            _ => new() { Kind = PluginPromptAttachmentKind.Metadata, DisplayName = item.GetType().Name, Metadata = metadata },
        };
    }
}
