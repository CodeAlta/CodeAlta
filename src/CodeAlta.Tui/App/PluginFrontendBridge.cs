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
            .ToArray();

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
