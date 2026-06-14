using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Styling;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Templating;

namespace CodeAlta.Views;

internal sealed class ModelProviderSelectorView
{
    public ModelProviderSelectorView(
        SessionWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        ModelProviderSelectorController controller)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(controller);

        ModelProviderSelect = new Select<ModelProviderOption>()
            .SelectedIndex(workspaceViewModel.Bind.SelectedModelProviderIndex)
            .MinWidth(14)
            .MaxWidth(22)
            .IsEnabled(workspaceViewModel.Bind.CanSelectModelProvider);
        ChatModelSelect = new Select<ChatModelOption>()
            .SelectedIndex(workspaceViewModel.Bind.SelectedModelIndex)
            .ItemTemplate(new DataTemplate<ChatModelOption>(
                static (DataTemplateValue<ChatModelOption> value, in DataTemplateContext _) =>
                    new Markup(ModelProviderPresentation.BuildModelOptionMarkup(value.GetValue()))
                    {
                        Wrap = false,
                    },
                null))
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
        ModelsDialogButton = new Button(new TextBlock("Model->") { Wrap = false, IsSelectable = false })
            .Click(controller.OpenModels);
        var modelsDialogButtonHost = ModelsDialogButton.Tooltip(new TextBlock(SR.T("Open the models dialog.")));
        var compactSessionButton = new Button(new TextBlock($"{TerminalIcons.MdSelectCompare}"))
            .Click(controller.CompactSession)
            .IsEnabled(promptComposerViewModel.Bind.CanCompact)
            .Tooltip(new TextBlock(SR.T("Compact the selected session when it is idle (Ctrl+F11).")));

        Root = new HStack(
        [
            modelsDialogButtonHost,
            ModelProviderSelect,
            ChatModelSelect,
            ChatReasoningSelect,
            compactSessionButton,
            AlwaysEnqueueCheckBox,
        ])
        {
            Spacing = 2,
        };
    }

    public Visual Root { get; }

    public Button ModelsDialogButton { get; }

    public Select<ModelProviderOption> ModelProviderSelect { get; }

    public Select<ChatModelOption> ChatModelSelect { get; }

    public Select<ChatReasoningOption> ChatReasoningSelect { get; }

    public CheckBox AlwaysEnqueueCheckBox { get; }

    public void SyncItems(SessionWorkspaceViewModel workspaceViewModel)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);

        using var _ = workspaceViewModel.SuppressSelectionChangedNotifications();
        SyncSelect(ModelProviderSelect, workspaceViewModel.ModelProviderOptions, workspaceViewModel.SelectedModelProviderIndex);
        SyncSelect(ChatModelSelect, workspaceViewModel.ModelOptions, workspaceViewModel.SelectedModelIndex);
        SyncSelect(ChatReasoningSelect, workspaceViewModel.ReasoningOptions, workspaceViewModel.SelectedReasoningIndex);
    }

    private static void SyncSelect<T>(Select<T> select, IReadOnlyList<T> items, int selectedIndex)
    {
        ModelProviderPresentation.ReplaceSelectItems(select, items);
        select.SelectedIndex = selectedIndex;
    }
}
