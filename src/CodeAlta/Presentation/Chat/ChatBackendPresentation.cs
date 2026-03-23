using CodeAlta.Agent;
using CodeAlta.Models;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Presentation.Chat;

internal static class ChatBackendPresentation
{
    public static Dictionary<string, ChatBackendState> CreateBackendStates()
    {
        return new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
        {
            [AgentBackendIds.Codex.Value] = new(AgentBackendIds.Codex, "Codex"),
            [AgentBackendIds.Copilot.Value] = new(AgentBackendIds.Copilot, "Copilot"),
        };
    }

    public static List<ChatBackendOption> BuildBackendOptions()
    {
        return
        [
            new ChatBackendOption(AgentBackendIds.Codex, "Codex"),
            new ChatBackendOption(AgentBackendIds.Copilot, "Copilot"),
        ];
    }

    public static List<ChatModelOption> BuildModelOptions(ChatBackendState backendState)
    {
        ArgumentNullException.ThrowIfNull(backendState);

        if (backendState.Models.Count == 0)
        {
            return [new ChatModelOption(null, "(default)")];
        }

        return backendState.Models
            .Select(model => new ChatModelOption(model.Id, model.DisplayName ?? model.Id))
            .ToList();
    }

    public static List<ChatReasoningOption> BuildReasoningOptions(AgentModelInfo? model)
    {
        var efforts = model?.SupportedReasoningEfforts is { Count: > 0 } supported
            ? supported
            : Enum.GetValues<AgentReasoningEffort>();

        return efforts
            .Distinct()
            .Select(static effort => new ChatReasoningOption(effort, SplitPascalCase(effort.ToString())))
            .ToList();
    }

    public static AgentBackendId ResolveBackendSelection(
        AgentBackendId currentSelection,
        AgentBackendId requestedBackend,
        bool adoptRequestedBackend)
        => adoptRequestedBackend ? requestedBackend : currentSelection;

    public static string BuildBackendStatusMarkup(
        IEnumerable<ChatBackendState> backendStates,
        AgentBackendId selectedBackendId,
        bool isInitializing)
    {
        ArgumentNullException.ThrowIfNull(backendStates);

        var items = backendStates
            .OrderBy(static state => state.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(state =>
            {
                var tone = state.Availability switch
                {
                    ChatBackendAvailability.Ready => "success",
                    ChatBackendAvailability.Unsupported or ChatBackendAvailability.Failed => "warning",
                    ChatBackendAvailability.Connecting => "primary",
                    _ => "muted",
                };
                var icon = state.Availability switch
                {
                    ChatBackendAvailability.Ready => $"{NerdFont.MdCheck}",
                    ChatBackendAvailability.Unsupported => $"{NerdFont.CodWarning}",
                    ChatBackendAvailability.Failed => $"{NerdFont.MdClose}",
                    ChatBackendAvailability.Connecting => $"{NerdFont.MdTimerOutline}",
                    _ => $"{NerdFont.MdHelpBox}",
                };
                var selected = string.Equals(state.BackendId.Value, selectedBackendId.Value, StringComparison.OrdinalIgnoreCase)
                    ? "[bold]"
                    : string.Empty;
                var reset = selected.Length > 0 ? "[/]" : string.Empty;
                return $"{selected}[{tone}]{icon} {AnsiMarkup.Escape(state.DisplayName)}[/]{reset}";
            });

        var prefix = isInitializing
            ? $"[primary]{NerdFont.MdTimerOutline} Detecting[/] "
            : string.Empty;
        return prefix + string.Join("   ", items);
    }

    public static string? ResolvePreferredModelId(
        IReadOnlyList<AgentModelInfo> models,
        string? preferredModelId)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (models.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredModelId) &&
            models.Any(model => string.Equals(model.Id, preferredModelId, StringComparison.Ordinal)))
        {
            return preferredModelId;
        }

        return models[0].Id;
    }

    public static AgentReasoningEffort? ResolvePreferredReasoningEffort(
        AgentModelInfo? model,
        AgentReasoningEffort? preferredReasoningEffort)
    {
        var supportedReasoningEfforts = model?.SupportedReasoningEfforts?
            .Distinct()
            .ToArray();
        if (preferredReasoningEffort is { } requestedEffort &&
            (supportedReasoningEfforts is null || supportedReasoningEfforts.Length == 0 || supportedReasoningEfforts.Contains(requestedEffort)))
        {
            return requestedEffort;
        }

        if (supportedReasoningEfforts is { Length: > 0 })
        {
            if (supportedReasoningEfforts.Contains(AgentReasoningEffort.High))
            {
                return AgentReasoningEffort.High;
            }

            if (model?.DefaultReasoningEffort is { } defaultEffort && supportedReasoningEfforts.Contains(defaultEffort))
            {
                return defaultEffort;
            }

            return supportedReasoningEfforts[0];
        }

        return model?.DefaultReasoningEffort;
    }

    public static string BuildReadyStatusMessage(ChatBackendState backendState)
    {
        ArgumentNullException.ThrowIfNull(backendState);

        var selectedModel = GetSelectedModel(backendState);
        if (selectedModel is not null)
        {
            return $"Connected · {selectedModel.DisplayName ?? selectedModel.Id}";
        }

        return backendState.Models.Count switch
        {
            0 => "Connected.",
            1 => $"Connected · {backendState.Models[0].DisplayName ?? backendState.Models[0].Id}",
            _ => $"Connected · {backendState.Models.Count} models",
        };
    }

    public static string BuildUnsupportedBackendMessage(ChatBackendState backendState, string message)
    {
        ArgumentNullException.ThrowIfNull(backendState);

        var trimmed = string.IsNullOrWhiteSpace(message) ? "CLI not found." : message.Trim();
        return $"{backendState.DisplayName} is unavailable: {trimmed}";
    }

    public static string BuildFailedBackendMessage(ChatBackendState backendState, string message)
    {
        ArgumentNullException.ThrowIfNull(backendState);

        var trimmed = string.IsNullOrWhiteSpace(message) ? "Failed to initialize backend." : message.Trim();
        return $"{backendState.DisplayName} failed: {trimmed}";
    }

    public static void ReplaceSelectItems<T>(Select<T> select, IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(items);

        if (HasSameItems(select.Items, items))
        {
            return;
        }

        select.Items.Clear();
        foreach (var item in items)
        {
            select.Items.Add(item);
        }
    }

    internal static AgentModelInfo? GetSelectedModel(ChatBackendState backendState)
    {
        return string.IsNullOrWhiteSpace(backendState.SelectedModelId)
            ? null
            : backendState.Models.FirstOrDefault(model =>
                string.Equals(model.Id, backendState.SelectedModelId, StringComparison.Ordinal));
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (index > 0 && char.IsUpper(ch) && !char.IsWhiteSpace(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool HasSameItems<T>(IReadOnlyList<T> currentItems, IReadOnlyList<T> newItems)
    {
        ArgumentNullException.ThrowIfNull(currentItems);
        ArgumentNullException.ThrowIfNull(newItems);

        if (currentItems.Count != newItems.Count)
        {
            return false;
        }

        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < currentItems.Count; index++)
        {
            if (!comparer.Equals(currentItems[index], newItems[index]))
            {
                return false;
            }
        }

        return true;
    }
}
