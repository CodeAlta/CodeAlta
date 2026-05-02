using CodeAlta.Agent;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Low-ceremony factories for command contributions.
/// </summary>
public static class Command
{
    /// <summary>Creates a prompt command.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="description">The command description.</param>
    /// <param name="handler">The command handler.</param>
    /// <param name="aliases">Optional aliases.</param>
    /// <returns>The command contribution.</returns>
    public static PluginCommandContribution Prompt(string name, string description, PluginCommandHandler handler, IReadOnlyList<string>? aliases = null)
        => Create(name, description, PluginCommandKind.Prompt, handler, aliases);

    /// <summary>Creates a prompt command from a one-parameter handler.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="description">The command description.</param>
    /// <param name="handler">The command handler.</param>
    /// <param name="aliases">Optional aliases.</param>
    /// <returns>The command contribution.</returns>
    public static PluginCommandContribution Prompt(string name, string description, Func<PluginCommandContext, ValueTask<PluginCommandResult>> handler, IReadOnlyList<string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Prompt(name, description, (context, _) => handler(context), aliases);
    }

    /// <summary>Creates a shell command.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="description">The command description.</param>
    /// <param name="handler">The command handler.</param>
    /// <param name="aliases">Optional aliases.</param>
    /// <returns>The command contribution.</returns>
    public static PluginCommandContribution Shell(string name, string description, PluginCommandHandler handler, IReadOnlyList<string>? aliases = null)
        => Create(name, description, PluginCommandKind.Shell, handler, aliases);

    /// <summary>Creates a thread command.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="description">The command description.</param>
    /// <param name="handler">The command handler.</param>
    /// <param name="aliases">Optional aliases.</param>
    /// <returns>The command contribution.</returns>
    public static PluginCommandContribution Thread(string name, string description, PluginCommandHandler handler, IReadOnlyList<string>? aliases = null)
        => Create(name, description, PluginCommandKind.Thread, handler, aliases) with
        {
            Availability = PluginCommandAvailability.ThreadSelected,
        };

    private static PluginCommandContribution Create(
        string name,
        string description,
        PluginCommandKind kind,
        PluginCommandHandler handler,
        IReadOnlyList<string>? aliases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(handler);
        return new PluginCommandContribution
        {
            Name = name,
            Label = ToLabel(name),
            Description = description,
            Kind = kind,
            Handler = handler,
            Aliases = aliases ?? [],
        };
    }

    private static string ToLabel(string name)
    {
        return string.Join(' ', name.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}

/// <summary>
/// Low-ceremony factories for prompt contributions.
/// </summary>
public static class Prompt
{
    /// <summary>Creates a developer prompt contribution with static content.</summary>
    /// <param name="content">The prompt content.</param>
    /// <param name="title">Optional title.</param>
    /// <param name="order">Ordering hint.</param>
    /// <returns>The prompt contribution.</returns>
    public static PluginSystemPromptContribution Developer(string content, string? title = null, int order = 0)
        => Static(PluginPromptChannel.Developer, content, title, order);

    /// <summary>Creates a system prompt contribution with static content.</summary>
    /// <param name="content">The prompt content.</param>
    /// <param name="title">Optional title.</param>
    /// <param name="order">Ordering hint.</param>
    /// <returns>The prompt contribution.</returns>
    public static PluginSystemPromptContribution System(string content, string? title = null, int order = 0)
        => Static(PluginPromptChannel.System, content, title, order);

    /// <summary>Creates a prompt contribution with dynamic content.</summary>
    /// <param name="channel">The prompt channel.</param>
    /// <param name="content">The content provider.</param>
    /// <param name="title">Optional title.</param>
    /// <param name="kind">Prompt part kind.</param>
    /// <param name="order">Ordering hint.</param>
    /// <returns>The prompt contribution.</returns>
    public static PluginSystemPromptContribution Dynamic(
        PluginPromptChannel channel,
        PluginSystemPromptContentProvider content,
        string? title = null,
        PluginPromptPartKind kind = PluginPromptPartKind.Guidance,
        int order = 0)
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

    private static PluginSystemPromptContribution Static(PluginPromptChannel channel, string content, string? title, int order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return Dynamic(channel, (_, _) => new ValueTask<string?>(content), title, PluginPromptPartKind.Guidance, order);
    }
}

/// <summary>
/// Low-ceremony factories for UI contributions.
/// </summary>
public static class PluginUi
{
    /// <summary>Creates a direct visual contribution.</summary>
    /// <param name="region">The UI region.</param>
    /// <param name="visual">The visual.</param>
    /// <param name="name">Optional natural name.</param>
    /// <param name="order">Ordering hint.</param>
    /// <returns>The UI contribution.</returns>
    public static PluginVisualContribution Visual(PluginUiRegion region, Visual visual, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(visual);
        return new PluginVisualContribution { Region = region, Visual = visual, Name = name, Order = order };
    }

    /// <summary>Creates a factory visual contribution.</summary>
    /// <param name="region">The UI region.</param>
    /// <param name="factory">The visual factory.</param>
    /// <param name="name">Optional natural name.</param>
    /// <param name="order">Ordering hint.</param>
    /// <returns>The UI contribution.</returns>
    public static PluginVisualContribution Visual(PluginUiRegion region, Func<PluginVisualContext, Visual?> factory, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new PluginVisualContribution { Region = region, CreateVisual = factory, Name = name, Order = order };
    }

    /// <summary>Creates a factory visual contribution from a parameterless factory.</summary>
    /// <param name="region">The UI region.</param>
    /// <param name="factory">The visual factory.</param>
    /// <param name="name">Optional natural name.</param>
    /// <param name="order">Ordering hint.</param>
    /// <returns>The UI contribution.</returns>
    public static PluginVisualContribution Visual(PluginUiRegion region, Func<Visual?> factory, string? name = null, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Visual(region, _ => factory(), name, order);
    }

    /// <summary>Creates a status contribution.</summary>
    /// <param name="label">The status label.</param>
    /// <param name="getText">The status text provider.</param>
    /// <param name="order">Ordering hint.</param>
    /// <returns>The status contribution.</returns>
    public static PluginStatusContribution Status(string label, Func<PluginStatusContext, string?> getText, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(getText);
        return new PluginStatusContribution
        {
            Region = PluginUiRegion.ThreadStatus,
            Name = label,
            Order = order,
            GetStatus = context => getText(context) is { } text ? new PluginStatusItem { Label = label, Text = text } : null,
        };
    }

    /// <summary>Creates a renderer contribution.</summary>
    /// <param name="region">The UI region.</param>
    /// <param name="target">The renderer target.</param>
    /// <param name="renderer">The renderer callback.</param>
    /// <param name="order">Ordering hint.</param>
    /// <returns>The renderer contribution.</returns>
    public static PluginRendererContribution Renderer(PluginUiRegion region, string? target, PluginRenderer renderer, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return new PluginRendererContribution { Region = region, Target = target, Renderer = renderer, Order = order };
    }
}

/// <summary>
/// Low-ceremony factories for resource contributions.
/// </summary>
public static class Resources
{
    /// <summary>Creates a skill-root resource.</summary>
    /// <param name="path">The resource path.</param>
    /// <param name="precedence">Resource precedence.</param>
    /// <returns>The resource contribution.</returns>
    public static PluginResourceContribution SkillRoot(string path, int precedence = 0) => Create(PluginResourceKind.SkillRoot, path, precedence);

    /// <summary>Creates a system-prompt-root resource.</summary>
    /// <param name="path">The resource path.</param>
    /// <param name="precedence">Resource precedence.</param>
    /// <returns>The resource contribution.</returns>
    public static PluginResourceContribution SystemPromptRoot(string path, int precedence = 0) => Create(PluginResourceKind.SystemPromptRoot, path, precedence);

    /// <summary>Creates a template-root resource.</summary>
    /// <param name="path">The resource path.</param>
    /// <param name="precedence">Resource precedence.</param>
    /// <returns>The resource contribution.</returns>
    public static PluginResourceContribution TemplateRoot(string path, int precedence = 0) => Create(PluginResourceKind.TemplateRoot, path, precedence);

    /// <summary>Creates a theme-root resource.</summary>
    /// <param name="path">The resource path.</param>
    /// <param name="precedence">Resource precedence.</param>
    /// <returns>The resource contribution.</returns>
    public static PluginResourceContribution ThemeRoot(string path, int precedence = 0) => Create(PluginResourceKind.ThemeRoot, path, precedence);

    /// <summary>Creates an MCP manifest resource.</summary>
    /// <param name="path">The resource path.</param>
    /// <param name="precedence">Resource precedence.</param>
    /// <returns>The resource contribution.</returns>
    public static PluginResourceContribution McpServerManifest(string path, int precedence = 0) => Create(PluginResourceKind.McpServerManifest, path, precedence);

    /// <summary>Creates an agent-definition-root resource.</summary>
    /// <param name="path">The resource path.</param>
    /// <param name="precedence">Resource precedence.</param>
    /// <returns>The resource contribution.</returns>
    public static PluginResourceContribution AgentDefinitionRoot(string path, int precedence = 0) => Create(PluginResourceKind.AgentDefinitionRoot, path, precedence);

    private static PluginResourceContribution Create(PluginResourceKind kind, string path, int precedence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new PluginResourceContribution
        {
            Kind = kind,
            Path = path,
            Precedence = precedence,
            IsPackageRelative = !System.IO.Path.IsPathRooted(path),
        };
    }
}

/// <summary>
/// Low-ceremony factories for agent tool contributions.
/// </summary>
public static class Tool
{
    /// <summary>Creates a plugin tool contribution from an existing definition.</summary>
    /// <param name="definition">The tool definition.</param>
    /// <param name="promptSnippet">Optional prompt snippet.</param>
    /// <param name="promptGuidance">Optional prompt guidance.</param>
    /// <param name="activationPolicy">Optional activation policy.</param>
    /// <param name="renderer">Optional renderer.</param>
    /// <returns>The tool contribution.</returns>
    public static PluginAgentToolContribution FromDefinition(
        AgentToolDefinition definition,
        string? promptSnippet = null,
        string? promptGuidance = null,
        PluginToolActivationPolicy? activationPolicy = null,
        PluginRenderer? renderer = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new PluginAgentToolContribution
        {
            Definition = definition,
            PromptSnippet = promptSnippet,
            PromptGuidance = promptGuidance,
            ActivationPolicy = activationPolicy ?? PluginToolActivationPolicy.Default,
            Renderer = renderer,
        };
    }
}

/// <summary>
/// Low-ceremony factories for backend/provider contributions.
/// </summary>
public static class PluginBackend
{
    /// <summary>Creates an agent backend contribution from a factory.</summary>
    /// <param name="name">The backend name.</param>
    /// <param name="factory">The backend factory.</param>
    /// <param name="displayName">Optional display name.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="capabilities">Declared capabilities.</param>
    /// <returns>The backend contribution.</returns>
    public static PluginAgentBackendContribution FromFactory(
        string name,
        PluginAgentBackendFactory factory,
        string? displayName = null,
        string? description = null,
        PluginAgentBackendCapabilities capabilities = PluginAgentBackendCapabilities.Default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        return new PluginAgentBackendContribution
        {
            Name = name,
            DisplayName = displayName ?? name,
            Description = description,
            Capabilities = capabilities,
            Factory = factory,
        };
    }
}
