using CodeAlta.Catalog;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using PluginPromptEditorContribution = CodeAlta.Plugins.Abstractions.PluginPromptEditorContribution;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Views;

internal sealed class CodeAltaShellSurfaceOptions
{
    public required CodeAltaShellViewModel ShellViewModel { get; init; }

    public required SessionWorkspaceViewModel WorkspaceViewModel { get; init; }

    public required PromptComposerViewModel PromptComposerViewModel { get; init; }

    public required SessionWorkspaceChromeController WorkspaceChromeController { get; init; }

    public required PromptComposerViewController PromptComposerController { get; init; }

    public required QueuedPromptStripController QueuedPromptController { get; init; }

    public required ModelProviderSelectorController ModelProviderSelectorController { get; init; }

    public required SessionTabHostController SessionTabHostController { get; init; }

    public required IProjectFileSearchService ProjectFileSearchService { get; init; }

    public required Func<string?> GetPromptReferenceProjectRoot { get; init; }

    public IReadOnlyList<PluginPromptEditorContribution> PromptEditorContributions { get; init; } = [];

    public required Func<string, SessionState?, PromptComposerSessionBinding> GetPromptComposerSession { get; init; }

    public required State<float> ThinkingAnimationPhase01 { get; init; }

    public PromptImageWorkspaceCallbacks? PromptImageCallbacks { get; init; }

    public required Visual Sidebar { get; init; }

    public required ShellCommandSurfaceCoordinator ShellCommandSurfaceCoordinator { get; init; }

    public Func<CommandBar, Visual?>? ComposePluginFooter { get; init; }

    public bool CommandBarMultiLine { get; init; }
}
