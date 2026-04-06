using System.Reflection;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Commands;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ThreadWorkspaceViewTests
{
    [TestMethod]
    public void SyncChatSelectorItems_ReplacesSelectItems()
    {
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new ThreadWorkspaceViewModel
        {
            BackendOptions = [new ChatBackendOption(new("codex"), "Codex")],
            ModelOptions = [new ChatModelOption("gpt-5", "GPT-5")],
            ReasoningOptions = [new ChatReasoningOption(Agent.AgentReasoningEffort.High, "High")],
        };
        var promptComposerViewModel = new PromptComposerViewModel();
        var view = new ThreadWorkspaceView(
            shellViewModel,
            workspaceViewModel,
            promptComposerViewModel,
            [],
            static () => new TextBlock(string.Empty),
            static () => { },
            static _ => { },
            static () => { },
            static () => { },
            static _ => { },
            static () => { },
            static () => { },
            static () => { },
            static _ => { },
            static _ => { },
            static _ => { },
            static (_, _) => { },
            static (_, _) => { },
            static () => { },
            static () => { },
            static () => { },
            static () => { },
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { },
            new State<string?>(string.Empty),
            new State<float>(0),
            static () => { });

        view.SyncChatSelectorItems(workspaceViewModel);

        var backendSelect = GetPrivateField<Select<ChatBackendOption>>(view, "ChatBackendSelect");
        var modelSelect = GetPrivateField<Select<ChatModelOption>>(view, "ChatModelSelect");
        var reasoningSelect = GetPrivateField<Select<ChatReasoningOption>>(view, "ChatReasoningSelect");

        Assert.AreEqual(1, backendSelect.Items.Count);
        Assert.AreEqual("Codex", backendSelect.Items[0].Label);
        Assert.AreEqual(1, modelSelect.Items.Count);
        Assert.AreEqual("GPT-5", modelSelect.Items[0].Label);
        Assert.AreEqual(1, reasoningSelect.Items.Count);
        Assert.AreEqual("High", reasoningSelect.Items[0].Label);

        workspaceViewModel.BackendOptions =
        [
            new ChatBackendOption(new("codex"), "Codex"),
            new ChatBackendOption(new("copilot"), "Copilot"),
        ];
        workspaceViewModel.ModelOptions = [new ChatModelOption("gpt-5.1", "GPT-5.1")];
        workspaceViewModel.ReasoningOptions = [new ChatReasoningOption(Agent.AgentReasoningEffort.Low, "Low")];

        view.SyncChatSelectorItems(workspaceViewModel);

        Assert.AreEqual(2, backendSelect.Items.Count);
        Assert.AreEqual("Copilot", backendSelect.Items[1].Label);
        Assert.AreEqual("GPT-5.1", modelSelect.Items[0].Label);
        Assert.AreEqual("Low", reasoningSelect.Items[0].Label);
    }

    [TestMethod]
    public void ThreadInput_UsesSlashCommandLabelsAndSearchText()
    {
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var closeTabBinding = new ThreadWorkspaceCommandBinding(
            ShellCommandCatalog.Get("CodeAlta.Thread.CloseTab"),
            static () => { });
        var steerBinding = new ThreadWorkspaceCommandBinding(
            ShellCommandCatalog.Get("CodeAlta.Thread.Steer"),
            static () => { });
        var view = new ThreadWorkspaceView(
            shellViewModel,
            workspaceViewModel,
            promptComposerViewModel,
            [closeTabBinding, steerBinding],
            static () => new TextBlock(string.Empty),
            static () => { },
            static _ => { },
            static () => { },
            static () => { },
            static _ => { },
            static () => { },
            static () => { },
            static () => { },
            static _ => { },
            static _ => { },
            static _ => { },
            static (_, _) => { },
            static (_, _) => { },
            static () => { },
            static () => { },
            static () => { },
            static () => { },
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { },
            new State<string?>(string.Empty),
            new State<float>(0),
            static () => { });

        var closeTabCommand = Assert.IsInstanceOfType<Command>(
            view.ThreadInput.Commands.Single(command => string.Equals(command.Id, "CodeAlta.Thread.CloseTab", StringComparison.Ordinal)));
        var steerCommand = Assert.IsInstanceOfType<Command>(
            view.ThreadInput.Commands.Single(command => string.Equals(command.Id, "CodeAlta.Thread.Steer", StringComparison.Ordinal)));

        Assert.AreEqual("/close_tab", closeTabCommand.LabelMarkup);
        Assert.AreEqual("close_tab", closeTabCommand.Name);
        StringAssert.Contains(closeTabCommand.SearchText, "/close_tab");
        StringAssert.Contains(closeTabCommand.SearchText, "/close");
        Assert.AreEqual("Steer", steerCommand.LabelMarkup);
        Assert.AreEqual(CommandPresentation.CommandBar, steerCommand.Presentation);
        Assert.IsNotNull(steerCommand.SearchText);
        Assert.IsFalse(steerCommand.SearchText.Contains("/steer", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToggleControls_UseCheckBoxesBoundToViewModels()
    {
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new ThreadWorkspaceViewModel
        {
            AutoScroll = false,
            CanToggleAutoScroll = true,
        };
        var promptComposerViewModel = new PromptComposerViewModel
        {
            AlwaysEnqueue = true,
            CanAlwaysEnqueue = true,
        };
        var view = new ThreadWorkspaceView(
            shellViewModel,
            workspaceViewModel,
            promptComposerViewModel,
            [],
            static () => new TextBlock(string.Empty),
            static () => { },
            static _ => { },
            static () => { },
            static () => { },
            static _ => { },
            static () => { },
            static () => { },
            static () => { },
            static _ => { },
            static _ => { },
            static _ => { },
            static (_, _) => { },
            static (_, _) => { },
            static () => { },
            static () => { },
            static () => { },
            static () => { },
            static _ => { },
            static _ => { },
            static _ => { },
            static _ => { },
            new State<string?>(string.Empty),
            new State<float>(0),
            static () => { });

        Assert.IsFalse(view.ChatAutoScrollCheckBox.IsChecked);
        Assert.IsTrue(view.ChatAutoScrollCheckBox.IsEnabled);
        Assert.IsTrue(view.AlwaysEnqueueCheckBox.IsChecked);
        Assert.IsTrue(view.AlwaysEnqueueCheckBox.IsEnabled);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
        where T : class
    {
        var property = instance.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (property is not null)
        {
            return Assert.IsInstanceOfType<T>(property.GetValue(instance));
        }

        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return Assert.IsInstanceOfType<T>(field.GetValue(instance));
    }
}
