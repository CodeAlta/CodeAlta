using CodeAlta.Agent;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Sidebar;
using CodeAlta.Tui.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using IntState = XenoAtom.Terminal.UI.State<int>;

namespace CodeAlta.Tui.App;

internal sealed class SessionUsageProjectionController
{
    private readonly SessionUsageViewModel _sessionUsageViewModel;
    private readonly Dictionary<string, ModelProviderState> _modelProviderStates;
    private readonly SessionSelectionContext _sessionSelection;
    private readonly ShellWorkspaceContext _workspaceContext;
    private readonly IntState _usageRefreshState;

    public SessionUsageProjectionController(
        SessionUsageViewModel sessionUsageViewModel,
        Dictionary<string, ModelProviderState> modelProviderStates,
        SessionSelectionContext sessionSelection,
        ShellWorkspaceContext workspaceContext,
        IntState usageRefreshState)
    {
        ArgumentNullException.ThrowIfNull(sessionUsageViewModel);
        ArgumentNullException.ThrowIfNull(modelProviderStates);
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(workspaceContext);
        ArgumentNullException.ThrowIfNull(usageRefreshState);

        _sessionUsageViewModel = sessionUsageViewModel;
        _modelProviderStates = modelProviderStates;
        _sessionSelection = sessionSelection;
        _workspaceContext = workspaceContext;
        _usageRefreshState = usageRefreshState;
    }

    public ComputedVisual CreateComputedVisual(Func<Visual> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new ComputedVisual(
            () =>
            {
                var _ = _usageRefreshState.Value;
                return build();
            });
    }

    public void ApplySessionUsageProjection()
    {
        _workspaceContext.DispatchToUiDeferred(
            () =>
            {
                SyncSelectedSessionUsageViewModel();
                _usageRefreshState.Value++;
            });
    }

    public void Refresh()
    {
        SyncSelectedSessionUsageViewModel();
        _usageRefreshState.Value++;
    }

    private void SyncSelectedSessionUsageViewModel()
    {
        _workspaceContext.VerifyBindableAccess();
        if (_sessionSelection.Selection.Target is WorkspaceTarget.Session)
        {
            var selectedSession = _sessionSelection.GetSelectedSession();
            if (selectedSession is null)
            {
                return;
            }

            var tab = _sessionSelection.EnsureSessionTab(selectedSession);
            _modelProviderStates.TryGetValue(tab.ProviderId.Value, out var providerState);
            _sessionUsageViewModel.Usage = AttachProviderModelLimits(
                tab.Usage,
                tab.ModelId ?? providerState?.SelectedModelId ?? tab.Usage?.LastOperation?.Model,
                providerState?.Models);
            _sessionUsageViewModel.ProviderName = ResolveProviderDisplayName(tab.ProviderId.Value, providerState);
            _sessionUsageViewModel.ModelName = tab.ModelId ?? providerState?.SelectedModelId;
            _sessionUsageViewModel.PluginTransientEvents = tab.PluginTransientEvents.Snapshot;
            return;
        }

        var providerId = _workspaceContext.GetPreferredModelProviderId();
        _modelProviderStates.TryGetValue(providerId.Value, out var draftProviderState);
        _sessionUsageViewModel.Usage = null;
        _sessionUsageViewModel.ProviderName = ResolveProviderDisplayName(providerId.Value, draftProviderState);
        _sessionUsageViewModel.ModelName = draftProviderState?.SelectedModelId;
        _sessionUsageViewModel.PluginTransientEvents = [];
    }

    private static string ResolveProviderDisplayName(string providerKey, ModelProviderState? providerState)
        => SidebarSessionPresentation.ResolveProviderDisplayName(providerKey, providerState?.DisplayName);

    private static AgentSessionUsage? AttachProviderModelLimits(
        AgentSessionUsage? usage,
        string? modelId,
        IReadOnlyList<AgentModelInfo>? models)
    {
        if (usage?.Window is null || usage.TokenLimit is > 0 || models is not { Count: > 0 })
        {
            return usage;
        }

        var model = FindModel(models, modelId) ?? FindModel(models, usage.LastOperation?.Model);
        if (model is null)
        {
            return usage;
        }

        var contextWindow = TryReadCapability(model.Capabilities, TokenCapabilityKind.ContextWindow);
        var inputTokenLimit = TryReadCapability(model.Capabilities, TokenCapabilityKind.InputTokenLimit);
        var outputTokenLimit = TryReadCapability(model.Capabilities, TokenCapabilityKind.OutputTokenLimit);
        var inputContextLimit = ResolveInputContextLimit(contextWindow, inputTokenLimit, outputTokenLimit);
        if (inputContextLimit is not > 0)
        {
            return usage;
        }

        return usage with
        {
            Window = usage.Window with
            {
                TokenLimit = inputContextLimit,
                TotalContextEnvelope = usage.Window.TotalContextEnvelope ?? contextWindow,
                MaxOutputTokens = usage.Window.MaxOutputTokens ?? outputTokenLimit,
            },
        };
    }

    private static AgentModelInfo? FindModel(IReadOnlyList<AgentModelInfo> models, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var normalized = modelId.Trim();
        return models.FirstOrDefault(model => IsModelMatch(model, normalized));
    }

    private static bool IsModelMatch(AgentModelInfo model, string modelId)
        => IsModelNameMatch(model.Id, modelId) || IsModelNameMatch(model.DisplayName, modelId);

    private static bool IsModelNameMatch(string? candidate, string modelId)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var normalizedCandidate = candidate.Trim();
        return string.Equals(normalizedCandidate, modelId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(StripDateSuffix(normalizedCandidate), modelId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedCandidate, StripDateSuffix(modelId), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripDateSuffix(string value)
    {
        const int DateSuffixLength = 11;
        if (value.Length <= DateSuffixLength || value[^DateSuffixLength] != '-')
        {
            return value;
        }

        var dateSlice = value.AsSpan(value.Length - 10);
        return IsIsoDate(dateSlice) ? value[..^DateSuffixLength] : value;
    }

    private static bool IsIsoDate(ReadOnlySpan<char> value)
        => value.Length == 10 &&
           char.IsDigit(value[0]) &&
           char.IsDigit(value[1]) &&
           char.IsDigit(value[2]) &&
           char.IsDigit(value[3]) &&
           value[4] == '-' &&
           char.IsDigit(value[5]) &&
           char.IsDigit(value[6]) &&
           value[7] == '-' &&
           char.IsDigit(value[8]) &&
           char.IsDigit(value[9]);

    private static long? ResolveInputContextLimit(long? contextWindow, long? inputTokenLimit, long? outputTokenLimit)
    {
        if (inputTokenLimit is > 0)
        {
            return inputTokenLimit.Value;
        }

        if (contextWindow is not > 0)
        {
            return null;
        }

        if (outputTokenLimit is > 0 && outputTokenLimit.Value < contextWindow.Value)
        {
            return Math.Max(contextWindow.Value - outputTokenLimit.Value, 1L);
        }

        return contextWindow.Value;
    }

    private static long? TryReadCapability(IReadOnlyDictionary<string, object?>? capabilities, TokenCapabilityKind kind)
    {
        if (capabilities is not { Count: > 0 })
        {
            return null;
        }

        return kind switch
        {
            TokenCapabilityKind.ContextWindow =>
                TryReadCapability(capabilities, "contextWindow") ??
                TryReadCapability(capabilities, "contextWindowTokens") ??
                TryReadCapability(capabilities, "context_length") ??
                TryReadCapability(capabilities, "contextLength") ??
                TryReadCapability(capabilities, "tokenLimit"),
            TokenCapabilityKind.InputTokenLimit =>
                TryReadCapability(capabilities, "inputTokenLimit") ??
                TryReadCapability(capabilities, "maxInputTokens"),
            TokenCapabilityKind.OutputTokenLimit =>
                TryReadCapability(capabilities, "outputTokenLimit") ??
                TryReadCapability(capabilities, "maxOutputTokens") ??
                TryReadCapability(capabilities, "maxTokens"),
            _ => null,
        };
    }

    private static long? TryReadCapability(IReadOnlyDictionary<string, object?> capabilities, string key)
    {
        return capabilities.TryGetValue(key, out var rawValue) &&
               TryConvertToInt64(rawValue, out var value) &&
               value > 0
            ? value
            : null;
    }

    private static bool TryConvertToInt64(object? value, out long converted)
    {
        switch (value)
        {
            case byte byteValue:
                converted = byteValue;
                return true;
            case sbyte sbyteValue:
                converted = sbyteValue;
                return true;
            case short shortValue:
                converted = shortValue;
                return true;
            case ushort ushortValue:
                converted = ushortValue;
                return true;
            case int intValue:
                converted = intValue;
                return true;
            case uint uintValue:
                converted = uintValue;
                return true;
            case long longValue:
                converted = longValue;
                return true;
            case ulong ulongValue when ulongValue <= long.MaxValue:
                converted = (long)ulongValue;
                return true;
            case float floatValue when floatValue is >= long.MinValue and <= long.MaxValue:
                converted = (long)floatValue;
                return true;
            case double doubleValue when doubleValue is >= long.MinValue and <= long.MaxValue:
                converted = (long)doubleValue;
                return true;
            case decimal decimalValue when decimalValue is >= long.MinValue and <= long.MaxValue:
                converted = (long)decimalValue;
                return true;
            case string stringValue when long.TryParse(stringValue, out var parsed):
                converted = parsed;
                return true;
            default:
                converted = default;
                return false;
        }
    }

    private enum TokenCapabilityKind
    {
        ContextWindow,
        InputTokenLimit,
        OutputTokenLimit,
    }
}
