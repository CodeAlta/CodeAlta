using CodeAlta.Agent;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Low-ceremony factories for command contributions.
/// </summary>
public static class Command
{
    /// <summary>Creates a prompt-editor command.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="description">The command description.</param>
    /// <param name="handler">The command handler.</param>
    /// <returns>The command contribution.</returns>
    public static PluginCommandContribution Prompt(string name, string description, PluginCommandHandler handler)
        => Create(name, description, PluginCommandPlacement.PromptEditor, handler);

    /// <summary>Creates a prompt-editor command from a one-parameter handler.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="description">The command description.</param>
    /// <param name="handler">The command handler.</param>
    /// <returns>The command contribution.</returns>
    public static PluginCommandContribution Prompt(string name, string description, Func<PluginCommandContext, ValueTask<PluginCommandResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Prompt(name, description, (context, _) => handler(context));
    }

    /// <summary>Creates a shell command.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="description">The command description.</param>
    /// <param name="handler">The command handler.</param>
    /// <returns>The command contribution.</returns>
    public static PluginCommandContribution Shell(string name, string description, PluginCommandHandler handler)
        => Create(name, description, PluginCommandPlacement.ShellRoot, handler);

    /// <summary>Creates a session command.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="description">The command description.</param>
    /// <param name="handler">The command handler.</param>
    /// <returns>The command contribution.</returns>
    public static PluginCommandContribution Session(string name, string description, PluginCommandHandler handler)
        => Create(name, description, PluginCommandPlacement.PromptEditor | PluginCommandPlacement.WorkspaceRoot, handler) with
        {
            Availability = PluginCommandAvailability.SessionSelected,
        };

    private static PluginCommandContribution Create(
        string name,
        string description,
        PluginCommandPlacement placement,
        PluginCommandHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(handler);
        return new PluginCommandContribution
        {
            Name = name,
            Label = ToLabel(name),
            Description = description,
            Placement = placement,
            Handler = handler,
        };
    }

    private static string ToLabel(string name)
    {
        return string.Join(' ', name.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}

/// <summary>
/// Low-ceremony factories for early startup contributions.
/// </summary>
public static class Startup
{
    /// <summary>Creates a startup hook contribution.</summary>
    /// <param name="name">The hook name.</param>
    /// <param name="handler">The startup handler.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="order">Ordering hint.</param>
    /// <returns>The startup contribution.</returns>
    public static PluginStartupContribution Hook(string name, PluginStartupHandler handler, string? description = null, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        return new PluginStartupContribution
        {
            Name = name,
            Description = description,
            Order = order,
            Handler = handler,
        };
    }

    /// <summary>Creates a startup contribution that exposes early resources.</summary>
    /// <param name="name">The contribution name.</param>
    /// <param name="resources">The early resources.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="order">Ordering hint.</param>
    /// <returns>The startup contribution.</returns>
    public static PluginStartupContribution Resources(string name, IReadOnlyList<PluginResourceContribution> resources, string? description = null, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(resources);
        return new PluginStartupContribution
        {
            Name = name,
            Description = description,
            Order = order,
            Resources = resources,
        };
    }
}

/// <summary>
/// Low-ceremony factories for resource contributions.
/// </summary>
public static class Resources
{
    /// <summary>Creates a skill-root resource contribution.</summary>
    public static PluginResourceContribution SkillRoot(string path, bool isPackageRelative = true)
        => Create(PluginResourceKind.SkillRoot, path, isPackageRelative);

    /// <summary>Creates a template-root resource contribution.</summary>
    public static PluginResourceContribution TemplateRoot(string path, bool isPackageRelative = true)
        => Create(PluginResourceKind.TemplateRoot, path, isPackageRelative);

    private static PluginResourceContribution Create(PluginResourceKind kind, string path, bool isPackageRelative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new PluginResourceContribution { Kind = kind, Path = path, IsPackageRelative = isPackageRelative };
    }
}

/// <summary>
/// Low-ceremony factories for agent tool contributions.
/// </summary>
public static class AgentTool
{
    /// <summary>Creates an agent tool contribution.</summary>
    public static PluginAgentToolContribution Create(AgentToolDefinition definition, string? promptSnippet = null, string? promptGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new PluginAgentToolContribution { Definition = definition, PromptSnippet = promptSnippet, PromptGuidance = promptGuidance };
    }
}

/// <summary>
/// Low-ceremony factories for system prompt contributions.
/// </summary>
public static class Prompt
{
    /// <summary>Creates a static developer prompt contribution.</summary>
    public static PluginSystemPromptContribution Developer(string content, string? title = null, PluginPromptPartKind kind = PluginPromptPartKind.Other, int order = 0)
        => Static(PluginPromptChannel.Developer, content, title, kind, order);

    /// <summary>Creates a static system prompt contribution.</summary>
    public static PluginSystemPromptContribution Static(PluginPromptChannel channel, string content, string? title = null, PluginPromptPartKind kind = PluginPromptPartKind.Other, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new PluginSystemPromptContribution
        {
            Channel = channel,
            Content = (_, _) => new ValueTask<string?>(content),
            Title = title,
            Kind = kind,
            Order = order,
        };
    }

    /// <summary>Creates a dynamic system prompt contribution.</summary>
    public static PluginSystemPromptContribution Dynamic(PluginPromptChannel channel, PluginSystemPromptContentProvider content, string? title = null, PluginPromptPartKind kind = PluginPromptPartKind.Other, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new PluginSystemPromptContribution
        {
            Channel = channel,
            Content = content,
            Title = title,
            Kind = kind,
            Order = order,
        };
    }
}

/// <summary>
/// Low-ceremony factories for UI contributions.
/// </summary>
public static class PluginUi
{
    /// <summary>Creates portable UI-region content. A null callback result intentionally contributes nothing.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="createContent"/> is null.</exception>
    public static PluginContentContribution Content(PluginUiRegion region, Func<PluginVisualContext, PluginRenderResult?> createContent, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(createContent);
        return new PluginContentContribution { Region = region, Name = name, Order = order, CreateContent = createContent };
    }

    /// <summary>Creates a status item contribution.</summary>
    public static PluginStatusContribution Status(PluginUiRegion region, PluginStatusItem item, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new PluginStatusContribution { Region = region, Name = name, Order = order, GetStatus = _ => item };
    }

    /// <summary>Creates a session-status contribution.</summary>
    public static PluginStatusContribution SessionStatus(string label, string text, PluginStatusTone tone = PluginStatusTone.Info, string? iconMarkup = null, string? name = null, int order = 0)
        => Status(
            PluginUiRegion.SessionStatus,
            new PluginStatusItem { Label = label, Text = text, Tone = tone, IconMarkup = iconMarkup },
            name,
            order);

    /// <summary>Creates a button for the desktop window. Set <see cref="PluginButtonContribution.Command"/> or <see cref="PluginButtonContribution.Canvas"/> with a <see langword="with"/> expression.</summary>
    /// <param name="place">Where the button is.</param>
    /// <param name="id">The identifier of the button in its plugin.</param>
    /// <param name="icon">A Lucide icon name, a brand logo name or the path of an SVG file of the plugin package.</param>
    /// <param name="label">The tooltip, the accessible name and the text of a menu line.</param>
    /// <param name="order">The ordering hint among the buttons of plugins at the same place.</param>
    /// <returns>The button, which is not valid until it names a command or a canvas.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/>, <paramref name="icon"/> or <paramref name="label"/> is null, empty or whitespace.</exception>
    public static PluginButtonContribution Button(PluginButtonPlace place, string id, string icon, string label, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(icon);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return new PluginButtonContribution { Place = place, Id = id, Icon = icon, Label = label, Name = id, Order = order };
    }

    /// <summary>Creates a renderer contribution.</summary>
    public static PluginRendererContribution Renderer(PluginUiRegion region, string? target, PluginRenderer renderer, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return new PluginRendererContribution { Region = region, Target = target, Renderer = renderer, Order = order };
    }

    /// <summary>Creates a notification dialog request.</summary>
    public static PluginDialogRequest NotifyDialog(string title, string message)
        => Dialog(PluginDialogKind.Notification, title, message);

    /// <summary>Creates a confirmation dialog request.</summary>
    public static PluginDialogRequest ConfirmDialog(string title, string message)
        => Dialog(PluginDialogKind.Confirmation, title, message) with
        {
            Buttons =
            [
                new PluginDialogButton { Name = "yes", Label = "Yes", IsDefault = true },
                new PluginDialogButton { Name = "no", Label = "No", IsCancel = true },
            ],
        };

    /// <summary>Creates an input dialog request.</summary>
    public static PluginDialogRequest InputDialog(string title, string? initialText = null)
        => Dialog(PluginDialogKind.Input, title, null) with { InitialText = initialText };

    /// <summary>Creates a text editor dialog request.</summary>
    public static PluginDialogRequest TextEditorDialog(string title, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Dialog(PluginDialogKind.TextEditor, title, null) with { InitialText = text };
    }

    /// <summary>Creates a custom dialog whose content is an HTML fragment for the desktop application.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="html">The HTML fragment; see <see cref="PluginHtml"/>.</param>
    /// <param name="buttons">The buttons that close the dialog; none shows a single Close button.</param>
    /// <returns>The dialog request.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="title"/> or <paramref name="html"/> is null, empty or whitespace.</exception>
    public static PluginDialogRequest HtmlDialog(string title, string html, params PluginDialogButton[] buttons)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        ArgumentNullException.ThrowIfNull(buttons);
        return Dialog(PluginDialogKind.Custom, title, null) with { Html = html, Buttons = buttons };
    }

    /// <summary>Creates a prompt picker opened by a trigger character.</summary>
    /// <param name="name">The contribution name.</param>
    /// <param name="trigger">The character that opens the picker.</param>
    /// <param name="title">The picker title.</param>
    /// <param name="search">The search handler.</param>
    /// <param name="placeholderText">Optional ready-prompt placeholder guidance.</param>
    /// <param name="order">The ordering hint.</param>
    /// <returns>The prompt picker contribution.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> or <paramref name="title"/> is null, empty or whitespace, or <paramref name="trigger"/> is a letter, a digit or white space.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="search"/> is null.</exception>
    public static PluginPromptPickerContribution PromptPicker(string name, char trigger, string title, PluginPromptPickerSearchHandler search, string? placeholderText = null, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(search);
        if (char.IsLetterOrDigit(trigger) || char.IsWhiteSpace(trigger) || char.IsControl(trigger) || char.IsSurrogate(trigger))
        {
            throw new ArgumentException("The trigger must be a punctuation or symbol character.", nameof(trigger));
        }

        return new PluginPromptPickerContribution { Name = name, Trigger = trigger, Title = title, SearchAsync = search, PlaceholderText = placeholderText, Order = order };
    }

    /// <summary>Creates a selection dialog request.</summary>
    public static PluginDialogRequest SelectionDialog(string title, IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new PluginDialogRequest
        {
            Kind = PluginDialogKind.Selection,
            Title = title,
            SelectionItems = items,
        };
    }

    private static PluginDialogRequest Dialog(PluginDialogKind kind, string title, string? message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return new PluginDialogRequest { Kind = kind, Title = title, Message = message };
    }
}
