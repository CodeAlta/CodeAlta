using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Templating;

namespace CodeAlta.Views;

internal sealed class AgentPromptSelectorView
{
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
        PromptDialogButton = new Button(new TextBlock(SR.T("Agent->")) { Wrap = false, IsSelectable = false })
            .Click(controller.OpenPrompts);
        var promptDialogButtonHost = PromptDialogButton.Tooltip(new TextBlock(SR.T("Open the agent prompts dialog.")));

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

    public void SyncItems(SessionWorkspaceViewModel workspaceViewModel)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        using var _ = workspaceViewModel.SuppressSelectionChangedNotifications();
        AgentPromptPresentation.ReplaceSelectItems(PromptSelect, workspaceViewModel.AgentPromptOptions);
        PromptSelect.SelectedIndex = workspaceViewModel.SelectedAgentPromptIndex;
    }
}
