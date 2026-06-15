using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Presentation.Chat;

internal static class ModelProviderPresentation
{
    public static Dictionary<string, ModelProviderState> CreateProviderStates()
    {
        return CreateProviderStates(
        [
            new ModelProviderDescriptor(ModelProviderIds.Codex, "Codex"),
            new ModelProviderDescriptor(ModelProviderIds.Copilot, "Copilot"),
        ]);
    }

    public static Dictionary<string, ModelProviderState> CreateProviderStates(
        IReadOnlyList<ModelProviderDescriptor> providerDescriptors)
    {
        ArgumentNullException.ThrowIfNull(providerDescriptors);

        return providerDescriptors.ToDictionary(
            static descriptor => descriptor.ProviderId.Value,
            static descriptor => new ModelProviderState(descriptor.ProviderId, descriptor.DisplayName),
            StringComparer.OrdinalIgnoreCase);
    }

    public static List<ModelProviderOption> BuildProviderOptions()
    {
        return BuildProviderOptions(
        [
            new ModelProviderDescriptor(ModelProviderIds.Codex, "Codex"),
            new ModelProviderDescriptor(ModelProviderIds.Copilot, "Copilot"),
        ]);
    }

    public static List<ModelProviderOption> BuildProviderOptions(
        IReadOnlyList<ModelProviderDescriptor> providerDescriptors)
    {
        ArgumentNullException.ThrowIfNull(providerDescriptors);

        return providerDescriptors
            .Select(static descriptor => new ModelProviderOption(descriptor.ProviderId, descriptor.DisplayName))
            .ToList();
    }

    public static List<ChatModelOption> BuildModelOptions(ModelProviderState providerState, string? selectedModelId = null)
    {
        ArgumentNullException.ThrowIfNull(providerState);
        var effectiveSelectedModelId = string.IsNullOrWhiteSpace(selectedModelId)
            ? providerState.SelectedModelId
            : selectedModelId.Trim();

        if (providerState.Models.Count == 0)
        {
            return string.IsNullOrWhiteSpace(effectiveSelectedModelId)
                ? [new ChatModelOption(null, SR.T("(default)"))]
                : [new ChatModelOption(effectiveSelectedModelId, effectiveSelectedModelId)];
        }

        var options = providerState.Models
            .Select(static model => new ChatModelOption(
                model.Id,
                string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName!))
            .ToList();
        if (!string.IsNullOrWhiteSpace(effectiveSelectedModelId) &&
            options.All(option => !string.Equals(option.ModelId, effectiveSelectedModelId, StringComparison.Ordinal)))
        {
            options.Insert(0, new ChatModelOption(effectiveSelectedModelId, effectiveSelectedModelId));
        }

        return options;
    }

    public static string BuildModelOptionMarkup(ChatModelOption option)
    {
        var label = string.IsNullOrWhiteSpace(option.Label) ? option.ModelId ?? string.Empty : option.Label;
        var id = string.IsNullOrWhiteSpace(option.ModelId) || string.Equals(label, option.ModelId, StringComparison.Ordinal)
            ? string.Empty
            : $" [dim]({AnsiMarkup.Escape(option.ModelId)})[/]";
        return $"{AnsiMarkup.Escape(label)}{id}";
    }

    public static List<ChatReasoningOption> BuildReasoningOptions(AgentModelInfo? model)
    {
        IEnumerable<AgentReasoningEffort> efforts = model?.SupportedReasoningEfforts switch
        {
            { Count: > 0 } supported => supported,
            { Count: 0 } => [AgentReasoningEffort.None],
            _ => Enum.GetValues<AgentReasoningEffort>(),
        };

        return efforts
            .Distinct()
            .Select(static effort => new ChatReasoningOption(effort, FormatReasoningEffort(effort)))
            .ToList();
    }

    public static ModelProviderId ResolveProviderSelection(
        ModelProviderId currentSelection,
        ModelProviderId requestedProvider,
        bool adoptRequestedProvider)
        => adoptRequestedProvider ? requestedProvider : currentSelection;

    public static string BuildProviderStatusMarkup(
        IEnumerable<ModelProviderState> providerStates,
        ModelProviderId selectedProviderId,
        bool isInitializing)
    {
        ArgumentNullException.ThrowIfNull(providerStates);

        var items = providerStates
            .OrderBy(static state => state.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(state =>
            {
                var tone = state.Availability switch
                {
                    ModelProviderAvailability.Ready => "success",
                    ModelProviderAvailability.Unsupported or ModelProviderAvailability.Failed => "warning",
                    ModelProviderAvailability.Probing => "primary",
                    _ => "muted",
                };
                var icon = state.Availability switch
                {
                    ModelProviderAvailability.Ready => $"{TerminalIcons.MdCheck}",
                    ModelProviderAvailability.Unsupported => $"{TerminalIcons.CodWarning}",
                    ModelProviderAvailability.Failed => $"{TerminalIcons.MdClose}",
                    ModelProviderAvailability.Probing => $"{TerminalIcons.MdTimerOutline}",
                    _ => $"{TerminalIcons.MdHelpBox}",
                };
                var selected = string.Equals(state.ProviderId.Value, selectedProviderId.Value, StringComparison.OrdinalIgnoreCase)
                    ? "[bold]"
                    : string.Empty;
                var reset = selected.Length > 0 ? "[/]" : string.Empty;
                return $"{selected}[{tone}]{icon} {AnsiMarkup.Escape(state.DisplayName)}[/]{reset}";
            });

        var prefix = isInitializing
            ? $"[primary]{TerminalIcons.MdTimerOutline} {SR.T("Detecting")}[/] "
            : string.Empty;
        return prefix + string.Join("   ", items);
    }

    public static string BuildProviderSummaryMarkup(
        IEnumerable<ModelProviderState> providerStates,
        bool isInitializing,
        IReadOnlyCollection<string>? configuredProviderKeys = null,
        int? configuredProviderCount = null)
    {
        ArgumentNullException.ThrowIfNull(providerStates);

        var states = providerStates.ToArray();
        if (isInitializing)
        {
            return $"[primary]{TerminalIcons.MdTimerOutline} {SR.T("Detecting providers")}[/]";
        }

        HashSet<string>? configuredKeySet = null;
        if (configuredProviderKeys is { Count: > 0 })
        {
            configuredKeySet = new HashSet<string>(
                configuredProviderKeys.Where(static key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
        }

        var providerCount = configuredProviderCount ?? configuredKeySet?.Count ?? states.Length;
        var stateErrorCount = states.Count(state =>
            state.Availability is ModelProviderAvailability.Unsupported or ModelProviderAvailability.Failed &&
            (configuredKeySet is null || configuredKeySet.Contains(state.ProviderId.Value)));
        var missingConfiguredCount = configuredKeySet?.Count(key =>
            !states.Any(state => string.Equals(state.ProviderId.Value, key, StringComparison.OrdinalIgnoreCase))) ?? 0;
        var errorCount = stateErrorCount + missingConfiguredCount;
        var readyCount = states.Count(static state => state.Availability == ModelProviderAvailability.Ready);
        var activeLabel = readyCount == 1 ? SR.T("active provider") : SR.T("active providers");
        var activeTone = readyCount > 0 ? "success" : "muted";
        var activeIcon = readyCount > 0
            ? $"{TerminalIcons.MdCheckCircleOutline}"
            : $"{TerminalIcons.MdTuneVariant}";
        var activeSegment = $"[{activeTone}]{activeIcon} {readyCount} {activeLabel}[/]";
        var errorSegment = errorCount > 0
            ? $" [warning]· {(errorCount == 1 ? SR.T("1 error") : SR.T("{0} errors", errorCount))}[/]"
            : string.Empty;
        var configuredSegment = providerCount != readyCount
            ? $" [dim]· {SR.T("{0} configured", providerCount)}[/]"
            : string.Empty;

        return $"{activeSegment}{configuredSegment}{errorSegment}";
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
            (supportedReasoningEfforts is null || supportedReasoningEfforts.Contains(requestedEffort)))
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

    public static string BuildReadyStatusMessage(ModelProviderState providerState)
    {
        ArgumentNullException.ThrowIfNull(providerState);

        var selectedModel = GetSelectedModel(providerState);
        if (selectedModel is not null)
        {
            return SR.T("Connected · {0}", selectedModel.DisplayName ?? selectedModel.Id);
        }

        var models = providerState.Models.ToArray();
        return models.Length switch
        {
            0 => SR.T("Connected."),
            1 => SR.T("Connected · {0}", models[0].DisplayName ?? models[0].Id),
            _ => SR.T("Connected · {0} models", models.Length),
        };
    }

    public static string BuildUnsupportedProviderMessage(ModelProviderState providerState, string message)
    {
        ArgumentNullException.ThrowIfNull(providerState);

        var trimmed = string.IsNullOrWhiteSpace(message) ? SR.T("CLI not found.") : message.Trim();
        return SR.T("{0} is unavailable: {1}", providerState.DisplayName, trimmed);
    }

    public static string BuildFailedProviderMessage(ModelProviderState providerState, string message)
    {
        ArgumentNullException.ThrowIfNull(providerState);

        var trimmed = string.IsNullOrWhiteSpace(message) ? SR.T("Failed to initialize provider.") : message.Trim();
        return SR.T("{0} failed: {1}", providerState.DisplayName, trimmed);
    }

    public static string FormatAvailability(ModelProviderAvailability availability)
        => availability switch
        {
            ModelProviderAvailability.Ready => SR.T("ready"),
            ModelProviderAvailability.Probing => SR.T("probing"),
            ModelProviderAvailability.Failed => SR.T("failed"),
            ModelProviderAvailability.Unsupported => SR.T("unsupported"),
            _ => SR.T("unknown"),
        };

    public static string FormatReasoningEffort(AgentReasoningEffort effort)
        => effort switch
        {
            AgentReasoningEffort.None => SR.T("None"),
            AgentReasoningEffort.Minimal => SR.T("Minimal"),
            AgentReasoningEffort.Low => SR.T("Low"),
            AgentReasoningEffort.Medium => SR.T("Medium"),
            AgentReasoningEffort.High => SR.T("High"),
            AgentReasoningEffort.XHigh => SR.T("X High"),
            _ => SR.T(SplitPascalCase(effort.ToString())),
        };

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

    internal static AgentModelInfo? GetSelectedModel(ModelProviderState providerState)
    {
        return string.IsNullOrWhiteSpace(providerState.SelectedModelId)
            ? null
            : providerState.Models.FirstOrDefault(model =>
                string.Equals(model.Id, providerState.SelectedModelId, StringComparison.Ordinal));
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
