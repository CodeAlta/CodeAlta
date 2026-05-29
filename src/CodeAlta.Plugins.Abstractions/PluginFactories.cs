using CodeAlta.Agent;
using XenoAtom.Terminal.UI;

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
    /// <summary>Creates a visual contribution.</summary>
    public static PluginVisualContribution Visual(PluginUiRegion region, Visual visual, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(visual);
        return new PluginVisualContribution { Region = region, Name = name, Order = order, Visual = visual };
    }

    /// <summary>Creates a visual contribution from a context factory.</summary>
    public static PluginVisualContribution Visual(PluginUiRegion region, Func<PluginVisualContext, Visual?> factory, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new PluginVisualContribution { Region = region, Name = name, Order = order, CreateVisual = factory };
    }

    /// <summary>Creates a visual contribution from a factory.</summary>
    public static PluginVisualContribution Visual(PluginUiRegion region, Func<Visual?> factory, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Visual(region, _ => factory(), name, order);
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

    /// <summary>Creates a custom visual dialog request.</summary>
    public static PluginDialogRequest CustomDialog(string title, Visual content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new PluginDialogRequest
        {
            Kind = PluginDialogKind.Custom,
            Title = title,
            Content = content,
        };
    }

    private static PluginDialogRequest Dialog(PluginDialogKind kind, string title, string? message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return new PluginDialogRequest { Kind = kind, Title = title, Message = message };
    }
}
