using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Styling;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Views;

internal sealed class ModelProviderSelectorView
{
    public ModelProviderSelectorView(
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        ModelProviderSelectorController controller)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(controller);

        ChatBackendSelect = new Select<ChatBackendOption>()
            .SelectedIndex(workspaceViewModel.Bind.SelectedModelProviderIndex)
            .MinWidth(14)
            .MaxWidth(22)
            .IsEnabled(workspaceViewModel.Bind.CanSelectModelProvider);
        ChatModelSelect = new Select<ChatModelOption>()
            .SelectedIndex(workspaceViewModel.Bind.SelectedModelIndex)
            .MinWidth(18)
            .MaxWidth(36)
            .IsEnabled(workspaceViewModel.Bind.CanSelectModel);
        ChatReasoningSelect = new Select<ChatReasoningOption>()
            .SelectedIndex(workspaceViewModel.Bind.SelectedReasoningIndex)
            .MinWidth(12)
            .MaxWidth(22)
            .IsEnabled(workspaceViewModel.Bind.CanSelectReasoning);
        AlwaysEnqueueCheckBox = new CheckBox("AlwaysQueue")
            .IsChecked(promptComposerViewModel.Bind.AlwaysEnqueue)
            .IsEnabled(promptComposerViewModel.Bind.CanAlwaysEnqueue);
        var compactThreadButton = new Button(new TextBlock($"{NerdFont.MdSelectCompare}"))
            .Click(controller.CompactThread)
            .IsEnabled(promptComposerViewModel.Bind.CanCompact)
            .Tooltip(new TextBlock("Compact the selected thread session when it is idle (Ctrl+F11)."));

        Root = new HStack(
        [
            ChatBackendSelect,
            ChatModelSelect,
            ChatReasoningSelect,
            compactThreadButton,
            AlwaysEnqueueCheckBox,
        ])
        {
            Spacing = 2,
        };
    }

    public Visual Root { get; }

    public Select<ChatBackendOption> ChatBackendSelect { get; }

    public Select<ChatModelOption> ChatModelSelect { get; }

    public Select<ChatReasoningOption> ChatReasoningSelect { get; }

    public CheckBox AlwaysEnqueueCheckBox { get; }

    public void SyncItems(ThreadWorkspaceViewModel workspaceViewModel)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);

        ChatBackendPresentation.ReplaceSelectItems(ChatBackendSelect, workspaceViewModel.ModelProviderOptions);
        ChatBackendPresentation.ReplaceSelectItems(ChatModelSelect, workspaceViewModel.ModelOptions);
        ChatBackendPresentation.ReplaceSelectItems(ChatReasoningSelect, workspaceViewModel.ReasoningOptions);
    }
}
