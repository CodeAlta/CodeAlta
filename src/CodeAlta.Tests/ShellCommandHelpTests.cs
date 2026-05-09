using CodeAlta.Frontend.Commands;
using CodeAlta.Frontend.Help;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellCommandHelpTests
{
    [TestMethod]
    public void BuildSections_UsesShellCommandCatalogMetadata()
    {
        var helpCommand = ShellCommandCatalog.Get("CodeAlta.Shell.Help");
        var paletteCommand = ShellCommandCatalog.Get("CodeAlta.Shell.CommandPalette");
        var exitCommand = ShellCommandCatalog.Get("CodeAlta.Shell.Exit");
        var openFolderCommand = ShellCommandCatalog.Get("CodeAlta.Project.OpenFolder");
        var editFileCommand = ShellCommandCatalog.Get("CodeAlta.File.Edit");
        var skillsCommand = ShellCommandCatalog.Get("CodeAlta.Skills.Manage");
        var pluginsCommand = ShellCommandCatalog.Get("CodeAlta.Plugins.Manage");
        var goToSidebarCommand = ShellCommandCatalog.Get("CodeAlta.Shell.FocusSidebar");
        var goToPromptCommand = ShellCommandCatalog.Get("CodeAlta.Shell.FocusPrompt");
        var modelCommand = ShellCommandCatalog.Get("CodeAlta.Shell.FocusModelProvider");
        var fullPromptCommand = ShellCommandCatalog.Get("CodeAlta.Thread.ExpandPrompt");
        var sendCommand = ShellCommandCatalog.Get("CodeAlta.Thread.Send");

        var sections = ShellHelpContentBuilder.BuildSections();
        var entry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, helpCommand.Label, StringComparison.Ordinal));
        var paletteEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, paletteCommand.Label, StringComparison.Ordinal));
        var openFolderEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, openFolderCommand.Label, StringComparison.Ordinal));
        var editFileEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, editFileCommand.Label, StringComparison.Ordinal));
        var skillsEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, skillsCommand.Label, StringComparison.Ordinal));
        var pluginsEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, pluginsCommand.Label, StringComparison.Ordinal));
        var exitEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, exitCommand.Label, StringComparison.Ordinal));
        var goToSidebarEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, goToSidebarCommand.Label, StringComparison.Ordinal));
        var goToPromptEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, goToPromptCommand.Label, StringComparison.Ordinal));
        var modelEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, modelCommand.Label, StringComparison.Ordinal));
        var fullPromptEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, fullPromptCommand.Label, StringComparison.Ordinal));
        var sendEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, sendCommand.Label, StringComparison.Ordinal));

        CollectionAssert.Contains(entry.Bindings.ToArray(), "/help");
        CollectionAssert.Contains(entry.Bindings.ToArray(), "?");
        CollectionAssert.Contains(paletteEntry.Bindings.ToArray(), "/");
        CollectionAssert.Contains(exitEntry.Bindings.ToArray(), "/exit");
        CollectionAssert.Contains(openFolderEntry.Bindings.ToArray(), "/open_folder");
        CollectionAssert.Contains(openFolderEntry.Bindings.ToArray(), "/open");
        CollectionAssert.Contains(editFileEntry.Bindings.ToArray(), "/edit");
        CollectionAssert.Contains(skillsEntry.Bindings.ToArray(), "/skills");
        CollectionAssert.Contains(skillsEntry.Bindings.ToArray(), "/skill");
        CollectionAssert.Contains(pluginsEntry.Bindings.ToArray(), ShellCommandCatalog.PluginsShortcutSequence.ToString()!);
        CollectionAssert.Contains(pluginsEntry.Bindings.ToArray(), "/plugins");
        CollectionAssert.Contains(pluginsEntry.Bindings.ToArray(), "/plugin");
        CollectionAssert.Contains(goToSidebarEntry.Bindings.ToArray(), "/go_to_sidebar");
        CollectionAssert.Contains(goToSidebarEntry.Bindings.ToArray(), "/sidebar");
        CollectionAssert.Contains(goToPromptEntry.Bindings.ToArray(), "/go_to_prompt");
        CollectionAssert.Contains(goToPromptEntry.Bindings.ToArray(), "/prompt");
        CollectionAssert.Contains(modelEntry.Bindings.ToArray(), "/model");
        CollectionAssert.Contains(fullPromptEntry.Bindings.ToArray(), "/full_prompt");
        CollectionAssert.Contains(sendEntry.Bindings.ToArray(), "/send");
    }

    [TestMethod]
    public void BuildSections_UsesHelpBindingsFromMetadata()
    {
        var sections = ShellHelpContentBuilder.BuildSections();
        var entriesByLabel = sections
            .SelectMany(static section => section.Entries)
            .ToDictionary(static entry => entry.Label, StringComparer.Ordinal);

        foreach (var command in ShellCommandCatalog.Commands.Where(static command => command.ShowInHelp))
        {
            Assert.IsTrue(entriesByLabel.TryGetValue(command.Label, out var entry), $"Missing help entry for {command.Id}.");
            CollectionAssert.AreEqual(command.HelpBindings.ToArray(), entry.Bindings.ToArray(), $"Help bindings diverged for {command.Id}.");
        }
    }

    [TestMethod]
    public void BuildSections_FilterMatchesAliases()
    {
        var sections = ShellHelpContentBuilder.BuildSections("compact");
        var entries = sections.SelectMany(static section => section.Entries).ToArray();

        Assert.AreEqual(1, entries.Length);
        Assert.AreEqual("Compact", entries[0].Label);
    }

    [TestMethod]
    public void BuildSections_KeyboardOnlyCommands_DoNotExposeSlashBindings()
    {
        var sections = ShellHelpContentBuilder.BuildSections();
        var steerEntry = sections
            .SelectMany(static section => section.Entries)
            .Single(candidate => string.Equals(candidate.Label, "Steer", StringComparison.Ordinal));

        CollectionAssert.DoesNotContain(steerEntry.Bindings.ToArray(), "/steer");
        CollectionAssert.Contains(steerEntry.Bindings.ToArray(), new KeyGesture(TerminalKey.F5).ToString()!);
    }

    [TestMethod]
    public void BuildMarkdown_FormatsHelpAsMarkdown()
    {
        var markdown = ShellHelpContentBuilder.BuildMarkdown();

        StringAssert.Contains(markdown, "# Shell Commands");
        StringAssert.Contains(markdown, "## General");
        StringAssert.Contains(markdown, "- **Help**");
        StringAssert.Contains(markdown, "`/help`");
        Assert.IsFalse(markdown.Contains("[bold]", StringComparison.Ordinal));
        Assert.IsFalse(markdown.Contains("[dim]", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildMarkdown_NoMatches_UsesMarkdownEmptyState()
    {
        var markdown = ShellHelpContentBuilder.BuildMarkdown("__no_such_help_entry__");

        StringAssert.Contains(markdown, "_No commands matched that filter._");
    }

    [TestMethod]
    public void ShellCommandMetadata_UsesCommandStyleIdentifiers()
    {
        var fullPromptCommand = ShellCommandCatalog.Get("CodeAlta.Thread.ExpandPrompt");
        var closeTabCommand = ShellCommandCatalog.Get("CodeAlta.Thread.CloseTab");
        var openFolderCommand = ShellCommandCatalog.Get("CodeAlta.Project.OpenFolder");
        var skillsCommand = ShellCommandCatalog.Get("CodeAlta.Skills.Manage");
        var tabLeftCommand = ShellCommandCatalog.Get("CodeAlta.Thread.TabLeft");
        var tabRightCommand = ShellCommandCatalog.Get("CodeAlta.Thread.TabRight");
        var steerCommand = ShellCommandCatalog.Get("CodeAlta.Thread.Steer");
        var exitCommand = ShellCommandCatalog.Get("CodeAlta.Shell.Exit");

        Assert.AreEqual("full_prompt", fullPromptCommand.CommandName);
        Assert.AreEqual("/full_prompt", fullPromptCommand.SlashCommandText);
        CollectionAssert.Contains(fullPromptCommand.Aliases.ToArray(), "full_prompt");
        CollectionAssert.Contains(fullPromptCommand.TextCommandAliases.ToArray(), "full_prompt");
        StringAssert.Contains(fullPromptCommand.CommandSearchText, "/full_prompt");
        Assert.AreEqual("close_tab", closeTabCommand.CommandName);
        Assert.AreEqual("/close_tab", closeTabCommand.SlashCommandText);
        CollectionAssert.Contains(closeTabCommand.Aliases.ToArray(), "close_tab");
        CollectionAssert.Contains(closeTabCommand.Aliases.ToArray(), "close");
        CollectionAssert.Contains(closeTabCommand.TextCommandAliases.ToArray(), "close");
        StringAssert.Contains(closeTabCommand.CommandSearchText, "/close_tab");
        StringAssert.Contains(closeTabCommand.CommandSearchText, "/close");
        Assert.AreEqual(new KeyGesture(TerminalChar.CtrlW, TerminalModifiers.Ctrl), closeTabCommand.Gesture);
        Assert.AreEqual("open_folder", openFolderCommand.CommandName);
        CollectionAssert.Contains(openFolderCommand.Aliases.ToArray(), "open");
        CollectionAssert.Contains(openFolderCommand.TextCommandAliases.ToArray(), "open");
        StringAssert.Contains(openFolderCommand.CommandSearchText, "/open");
        Assert.AreEqual("skills", skillsCommand.CommandName);
        CollectionAssert.Contains(skillsCommand.Aliases.ToArray(), "skill");
        CollectionAssert.Contains(skillsCommand.TextCommandAliases.ToArray(), "skill");
        StringAssert.Contains(skillsCommand.CommandSearchText, "/skill");
        Assert.AreEqual("tab_left", tabLeftCommand.CommandName);
        Assert.AreEqual("/tab_left", tabLeftCommand.SlashCommandText);
        CollectionAssert.Contains(tabLeftCommand.Aliases.ToArray(), "tab_left");
        CollectionAssert.Contains(tabLeftCommand.TextCommandAliases.ToArray(), "tab_left");
        StringAssert.Contains(tabLeftCommand.CommandSearchText, "/tab_left");
        Assert.AreEqual(new KeyGesture(TerminalKey.Left, TerminalModifiers.Alt), tabLeftCommand.Gesture);
        Assert.AreEqual("tab_right", tabRightCommand.CommandName);
        Assert.AreEqual("/tab_right", tabRightCommand.SlashCommandText);
        CollectionAssert.Contains(tabRightCommand.Aliases.ToArray(), "tab_right");
        CollectionAssert.Contains(tabRightCommand.TextCommandAliases.ToArray(), "tab_right");
        StringAssert.Contains(tabRightCommand.CommandSearchText, "/tab_right");
        Assert.AreEqual(new KeyGesture(TerminalKey.Right, TerminalModifiers.Alt), tabRightCommand.Gesture);
        Assert.AreEqual("Full Prompt", fullPromptCommand.DisplayLabelMarkup);
        Assert.AreEqual("Close Tab", closeTabCommand.DisplayLabelMarkup);
        Assert.AreEqual("Steer", steerCommand.DisplayLabelMarkup);
        Assert.AreEqual(0, steerCommand.TextCommandAliases.Count);
        Assert.IsFalse(steerCommand.CommandSearchText.Contains("/steer", StringComparison.Ordinal));
        Assert.AreEqual("Exit", exitCommand.DisplayLabelMarkup);
    }

    [TestMethod]
    public void ShellCommandCatalog_RequiresUniqueIdsAndAvailabilityMetadata()
    {
        var duplicateIds = ShellCommandCatalog.Commands
            .GroupBy(static command => command.Id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), duplicateIds);
        foreach (var command in ShellCommandCatalog.Commands)
        {
            StringAssert.StartsWith(command.Id, "CodeAlta.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(command.Label));
            Assert.IsFalse(string.IsNullOrWhiteSpace(command.Description));
            Assert.IsTrue(Enum.IsDefined(command.Availability), $"{command.Id} has an undefined availability value.");
        }
    }
}
