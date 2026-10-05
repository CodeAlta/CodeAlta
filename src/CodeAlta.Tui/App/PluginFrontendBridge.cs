using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using CodeAlta.Tui.Plugins;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.App;

internal sealed class PluginFrontendBridge
{
    private readonly PluginRuntimeManager _runtime;
    private readonly Func<ProjectDescriptor?> _getCurrentProject;
    private readonly TerminalPluginContributionAdapter _terminalAdapter;

    public PluginFrontendBridge(PluginRuntimeManager runtime, Func<ProjectDescriptor?> getCurrentProject)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(getCurrentProject);

        _runtime = runtime;
        _getCurrentProject = getCurrentProject;
        _terminalAdapter = new TerminalPluginContributionAdapter(runtime.Adapter);
    }

    public IReadOnlyList<PluginResolvedResourceContribution> GetResources()
        => _runtime.Adapter.GetResources(_runtime.ActivePlugins, CreateOptions());

    public IReadOnlyList<PluginCommandContribution> GetCommandContributions()
        => _runtime.Adapter.GetContributions<PluginCommandContribution>(PluginPoint.Command, CreateOptions())
            .Select(static registration => (PluginCommandContribution)registration.Contribution)
            .ToArray();

    public IReadOnlyList<PluginPromptEditorContribution> GetPromptEditorContributions()
        => _runtime.Adapter.GetContributions<PluginPromptEditorContribution>(PluginPoint.PromptEditor, CreateOptions())
            .Select(static registration => (PluginPromptEditorContribution)registration.Contribution)
            .Concat(CreatePromptPickerEditors())
            .ToArray();

    // A prompt picker is shown by the application: here as a search dialog attached to the prompt editor.
    // One picker per character, and none on the characters CodeAlta uses itself.
    private IEnumerable<PluginPromptEditorContribution> CreatePromptPickerEditors()
    {
        var triggers = new HashSet<char>();
        foreach (var registration in _runtime.Adapter.GetContributions<PluginPromptPickerContribution>(PluginPoint.PromptPicker, CreateOptions()))
        {
            var picker = (PluginPromptPickerContribution)registration.Contribution;
            if (picker.Trigger is '@' or '#' or '/' || !triggers.Add(picker.Trigger)) continue;
            yield return PluginTui.PromptEditor(
                "prompt-picker:" + picker.Name,
                host => new TerminalPromptPickerAttachment(host, picker, (query, cancellationToken) => SearchPromptPickerAsync(picker, query, cancellationToken)),
                picker.PlaceholderText,
                picker.Order);
        }
    }

    private async Task<IReadOnlyList<PluginPromptPickerItem>?> SearchPromptPickerAsync(PluginPromptPickerContribution picker, string query, CancellationToken cancellationToken)
    {
        var (items, diagnostics) = await _runtime.Adapter.SearchPromptPickerAsync(_runtime.ActivePlugins, picker, query, CreateOptions(), cancellationToken).ConfigureAwait(false);
        return diagnostics.Count > 0 ? null : [.. items.Where(static item => item is not null && !string.IsNullOrEmpty(item.Label) && item.InsertText is not null).Take(MaximumPromptPickerItems)];
    }

    private const int MaximumPromptPickerItems = 50;

    public IReadOnlyList<string> GetPromptPlaceholderContributions()
        => GetPromptEditorContributions()
            .Select(static contribution => contribution.PlaceholderText)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Select(static text => text!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<PluginStatusItem> GetStatusItems(PluginUiRegion region, string? sessionId = null)
        => _runtime.Adapter.GetStatusItems(_runtime.ActivePlugins, region, CreateOptions(sessionId))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Text))
            .ToArray();

    public IReadOnlyList<Visual> CreateVisuals(PluginUiRegion region, string? sessionId = null)
        => _terminalAdapter.CreateVisuals(_runtime.ActivePlugins, region, CreateOptions(sessionId));

    public Task<(IReadOnlyList<PluginTerminalRenderResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> RenderAsync(
        PluginUiRegion region,
        string? target,
        object? payload,
        CancellationToken cancellationToken = default)
        => _terminalAdapter.RenderAsync(_runtime.ActivePlugins, region, target, payload, CreateOptions(), cancellationToken).AsTask();

    public async Task<PluginCommandResult> ExecuteCommandAsync(PluginCommandContribution contribution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        var result = await _runtime.Adapter.ExecuteCommandAsync(
                _runtime.ActivePlugins,
                contribution,
                CreateOptions(),
                cancellationToken);
        return result.Result;
    }

    private PluginAdapterOperationOptions CreateOptions(string? sessionId = null)
    {
        var project = _getCurrentProject();
        return new PluginAdapterOperationOptions
        {
            ProjectId = project?.Id,
            ProjectPath = project?.ProjectPath,
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            HasInteractiveUi = true,
            SupportsTerminalVisuals = true,
        };
    }
}
