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

    public required ThreadWorkspaceViewModel WorkspaceViewModel { get; init; }

    public required PromptComposerViewModel PromptComposerViewModel { get; init; }

    public required IReadOnlyList<ThreadWorkspaceCommandBinding> WorkspaceCommandBindings { get; init; }

    public required ThreadWorkspaceChromeController WorkspaceChromeController { get; init; }

    public required PromptComposerViewController PromptComposerController { get; init; }

    public required QueuedPromptStripController QueuedPromptController { get; init; }

    public required ModelProviderSelectorController ModelProviderSelectorController { get; init; }

    public required ThreadTabHostController ThreadTabHostController { get; init; }

    public required IProjectFileSearchService ProjectFileSearchService { get; init; }

    public required Func<string?> GetPromptReferenceProjectRoot { get; init; }

    public IReadOnlyList<PluginPromptEditorContribution> PromptEditorContributions { get; init; } = [];

    public required Func<string, ThreadSessionState?, PromptComposerSessionBinding> GetPromptComposerSession { get; init; }

    public required State<float> ThinkingAnimationPhase01 { get; init; }

    public PromptImageWorkspaceCallbacks? PromptImageCallbacks { get; init; }

    public required Visual Sidebar { get; init; }

    public required ShellCommandSurfaceCoordinator ShellCommandSurfaceCoordinator { get; init; }

    public required Action OpenAcpManager { get; init; }

    public required Action ToggleTerminalLoopCallback { get; init; }

    public required Action ToggleNavigator { get; init; }

    public required Func<bool> CanUseCommandPalette { get; init; }

    public Func<CommandBar, Visual?>? ComposePluginFooter { get; init; }

    public bool CommandBarMultiLine { get; init; }
}
