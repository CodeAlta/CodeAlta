using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Chat;
using CodeAlta.Tui.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Templating;

namespace CodeAlta.Tui.Views;

internal sealed class AgentPromptSelectorView
{
    private readonly TextBlock _promptDialogButtonText;
    private readonly TextBlock _promptDialogButtonTooltip;

    public AgentPromptSelectorView(SessionWorkspaceViewModel workspaceViewModel, AgentPromptSelectorController controller)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(controller);

        PromptSelect = new Select<AgentPromptOption>()
            .SelectedIndex(workspaceViewModel.Bind.SelectedAgentPromptIndex)
            .ItemTemplate(new DataTemplate<AgentPromptOption>(
                static (DataTemplateValue<AgentPromptOption> value, in DataTemplateContext _) =>
                    new Markup(AgentPromptPresentation.BuildPromptOptionMarkup(value.GetValue()))
                    {
                        Wrap = false,
                    },
                null))
            .MinWidth(18)
            .MaxWidth(40)
            .IsEnabled(workspaceViewModel.Bind.CanSelectAgentPrompt);
        _promptDialogButtonText = new TextBlock(SR.T("Agent->")) { Wrap = false, IsSelectable = false };
        _promptDialogButtonTooltip = new TextBlock(SR.T("Open the agent prompts dialog."));
        PromptDialogButton = new Button(_promptDialogButtonText)
            .Click(controller.OpenPrompts);
        var promptDialogButtonHost = PromptDialogButton.Tooltip(_promptDialogButtonTooltip);

        Root = new HStack(
        [
            promptDialogButtonHost,
            PromptSelect,
        ])
        {
            Spacing = 1,
        };
    }

    public Visual Root { get; }

    public Button PromptDialogButton { get; }

    public Select<AgentPromptOption> PromptSelect { get; }

    public void RefreshLocalizedText()
    {
        _promptDialogButtonText.Text = SR.T("Agent->");
        _promptDialogButtonTooltip.Text = SR.T("Open the agent prompts dialog.");
    }

    public void SyncItems(SessionWorkspaceViewModel workspaceViewModel)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        using var _ = workspaceViewModel.SuppressSelectionChangedNotifications();
        AgentPromptPresentation.ReplaceSelectItems(PromptSelect, workspaceViewModel.AgentPromptOptions);
        PromptSelect.SelectedIndex = workspaceViewModel.SelectedAgentPromptIndex;
    }
}
