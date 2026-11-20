namespace CodeAlta.Plugins.Abstractions;

/// <summary>What a notification tells, which gives its color and its icon.</summary>
public enum PluginNotificationTone
{
    /// <summary>A fact, with nothing to do.</summary>
    Info,

    /// <summary>Something that ended well.</summary>
    Success,

    /// <summary>Something to look at.</summary>
    Warning,

    /// <summary>Something that failed.</summary>
    Danger,
}

/// <summary>
/// A notification with a title, which <see cref="IPluginUiService.NotifyAsync(PluginNotification, CancellationToken)"/> shows: in
/// CodeAlta Desktop, a notification of the system while no window of CodeAlta has the focus (the one of the sessions that are
/// done or wait), and a notice in the window otherwise.
/// </summary>
public sealed record PluginNotification
{
    /// <summary>The longest title every system shows whole.</summary>
    public const int MaximumTitleLength = 63;

    /// <summary>The longest body every system shows whole.</summary>
    public const int MaximumBodyLength = 255;

    /// <summary>Gets the title: what it is about, such as the name of an automation. A notification of the system cuts a longer one to <see cref="MaximumTitleLength"/>.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the text under the title, or <see langword="null"/> for none. A notification of the system cuts a longer one to <see cref="MaximumBodyLength"/>.</summary>
    public string? Body { get; init; }

    /// <summary>Gets what it tells, which gives its color in the window.</summary>
    public PluginNotificationTone Tone { get; init; } = PluginNotificationTone.Info;

    /// <summary>Gets the session a click on the notification opens, or <see langword="null"/> to bring CodeAlta to the front.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the notification as one line of text, for a host that shows only a message: its title, then its body.</summary>
    /// <returns>The title, followed by the body when there is one.</returns>
    public string ToMessage() => string.IsNullOrWhiteSpace(Body) ? Title.Trim() : $"{Title.Trim()}: {Body.Trim()}";
}
