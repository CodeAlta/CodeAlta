using CodeAlta.Credentials;
using CodeAlta.Desktop.Rpc;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Notifications;

namespace CodeAlta.Desktop;

/// <summary>
/// Tells the user, while CodeAlta is in the background, that a use of a secret that asks before use waits for their answer: a
/// notification of the system that brings the window back, where its prompt asks to confirm or deny the use, and the attention of
/// the application (the Dock icon bounces, the taskbar button flashes). It follows the setting of the alerts of the sessions
/// (<see cref="DesktopTurnAlertMode"/>), and removes the notification of a use once it no longer waits.
/// </summary>
internal sealed class DesktopSecretPrompts : IDisposable
{
    /// <summary>The start of the identity of the notifications of the secrets; the rest is a number.</summary>
    internal const string NotificationPrefix = "codealta.secret.";

    private const int MaximumTitle = 63;
    private const int MaximumBody = 255;

    private readonly IDesktopTurnAlertSurface _surface;
    private readonly Func<IReadOnlyList<PendingSecretUse>> _pending;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // The uses the user was told about, with the identity of their notification; null for one told without a notification.
    private readonly Dictionary<(string Name, string? Use), string?> _told = [];
    private readonly CredentialBroker? _broker;
    private int _next;
    private bool _disposed;

    /// <summary>Creates the prompts of the secrets over the uses that wait and what tells the user.</summary>
    /// <param name="surface">The windows, the notifications and the mark of the application.</param>
    /// <param name="pending">Reads the uses that wait for a confirmation.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DesktopSecretPrompts(IDesktopTurnAlertSurface surface, Func<IReadOnlyList<PendingSecretUse>> pending)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(pending);
        _surface = surface;
        _pending = pending;
    }

    /// <summary>Creates the prompts of the secrets of a broker, read again each time its uses that wait change.</summary>
    /// <param name="surface">The windows, the notifications and the mark of the application.</param>
    /// <param name="broker">The credential broker of the host.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DesktopSecretPrompts(IDesktopTurnAlertSurface surface, CredentialBroker broker)
        : this(surface, (broker ?? throw new ArgumentNullException(nameof(broker))).ListPending)
    {
        _broker = broker;
        broker.PendingUsesChanged += OnPendingUsesChanged;
    }

    /// <summary>Tells whether a notification of the system is one of these prompts, and the session whose shell asks.</summary>
    /// <param name="activation">The activation of a notification.</param>
    /// <param name="sessionId">The session whose shell asks; null for another use.</param>
    /// <returns>True for a notification of a secret the user opened.</returns>
    internal static bool IsPrompt(NeoNotificationActivation activation, out string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(activation);
        sessionId = null;
        if (activation.Dismissed || !activation.NotificationId.StartsWith(NotificationPrefix, StringComparison.Ordinal)) return false;
        sessionId = activation.ActivationData is { Length: > 0 } data ? data : null;
        return true;
    }

    /// <summary>Says who asks for a secret, as the user reads it: the shell of a session by its title, a provider, a server.</summary>
    /// <param name="use">The use.</param>
    /// <param name="sessionTitle">The title of the session whose shell asks; null when it is not known.</param>
    /// <returns>The words, such as <c>the shell of the session “Fix the parser”</c>.</returns>
    internal static string WhoAsks(string? use, string? sessionTitle)
    {
        if (use is null) return "a use nobody named";
        if (CodeAlta.Orchestration.Hosting.CodeAltaHost.IsShellUse(use, out var sessionId))
            return sessionId is null ? "the shell of a session" : $"the shell of the session “{(string.IsNullOrWhiteSpace(sessionTitle) ? sessionId : sessionTitle.ReplaceLineEndings(" ").Trim())}”";
        return "the " + use;
    }

    /// <summary>The notification of a use that waits; never a value.</summary>
    /// <param name="id">The identity of the notification.</param>
    /// <param name="pending">The use.</param>
    /// <param name="sessionTitle">The title of the session whose shell asks; null when it is not known.</param>
    /// <returns>The notification.</returns>
    internal static NeoNotificationRequest NotificationOf(string id, PendingUseView pending, string? sessionTitle)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return NotificationOf(id, [pending], sessionTitle);
    }

    /// <summary>The notification of the secrets one use waits for, such as all those the shell of a session that starts asks for; never a value.</summary>
    /// <param name="id">The identity of the notification.</param>
    /// <param name="pending">The secrets of that use that wait: at least one.</param>
    /// <param name="sessionTitle">The title of the session whose shell asks; null when it is not known.</param>
    /// <returns>The notification.</returns>
    /// <exception cref="ArgumentException"><paramref name="pending"/> is empty.</exception>
    internal static NeoNotificationRequest NotificationOf(string id, IReadOnlyList<PendingUseView> pending, string? sessionTitle)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.Count == 0) throw new ArgumentException("No use waits.", nameof(pending));
        var first = pending[0];
        var several = pending.Count > 1;
        var title = several ? $"Use {pending.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} secrets?" : $"Use the secret {first.Name}?";
        var body = several
            ? $"{Capitalized(WhoAsks(first.Use, sessionTitle))} asks for {string.Join(", ", pending.Select(static use => use.Name))}. Open CodeAlta to confirm or deny."
            : $"{Capitalized(WhoAsks(first.Use, sessionTitle))} asks for it. Open CodeAlta to confirm or deny.";
        return new NeoNotificationRequest { Id = id, Title = Cut(title, MaximumTitle), Body = Cut(body, MaximumBody), ActivationData = first.SessionId ?? string.Empty };
    }

    /// <summary>Reads the uses that wait, tells the user of the new ones while the application is in the background, and removes the notifications of those that no longer wait.</summary>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>A task that ends once it is done.</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            var prompted = _pending().Select(SecretsService.PendingOf).Where(static pending => pending.Prompt).ToArray();
            var current = prompted.Select(static pending => (pending.Name.ToUpperInvariant(), pending.Use)).ToHashSet();
            foreach (var gone in _told.Keys.Where(key => !current.Contains(key)).ToArray())
            {
                // A notification names the secrets one use waits for: it goes with the last of them.
                if (_told.Remove(gone, out var id) && id is not null && !_told.ContainsValue(id))
                    await Try(token => _surface.RemoveNotificationAsync(id, token), cancellationToken).ConfigureAwait(false);
            }

            var fresh = prompted.Where(pending => !_told.ContainsKey((pending.Name.ToUpperInvariant(), pending.Use))).ToArray();
            if (fresh.Length == 0) return;
            var mode = _surface.Mode;
            // The window shows its prompt: a user in front of it needs nothing more.
            var background = !_surface.IsForeground && mode != DesktopTurnAlertMode.Off;
            var titles = background ? _surface.ReadSessions().ToDictionary(static session => session.SessionId, static session => session.Title, StringComparer.OrdinalIgnoreCase) : null;
            // One notification for what one use asks for: the shell of a session that starts names all its secrets at once.
            foreach (var asked in fresh.GroupBy(static pending => pending.Use, StringComparer.Ordinal))
            {
                var uses = asked.ToArray();
                string? id = null;
                if (background && mode == DesktopTurnAlertMode.All)
                {
                    id = NotificationPrefix + (++_next).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var title = uses[0].SessionId is { } session ? titles!.GetValueOrDefault(session) : null;
                    if (await Try(token => _surface.NotifyAsync(NotificationOf(id, uses, title), token), cancellationToken).ConfigureAwait(false) != NeoDesktopStatus.Success) id = null;
                }

                foreach (var pending in uses) _told[(pending.Name.ToUpperInvariant(), pending.Use)] = id;
            }

            if (background) await Try(_surface.RequestAttentionAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_broker is not null) _broker.PendingUsesChanged -= OnPendingUsesChanged;
        _disposed = true;
    }

    private void OnPendingUsesChanged(object? sender, EventArgs args) => _ = RefreshSafelyAsync();

    private async Task RefreshSafelyAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _surface.Warn($"The prompt of a secret that asks before use could not be shown: {exception.Message}");
        }
    }

    private static async Task<NeoDesktopStatus> Try(Func<CancellationToken, ValueTask<NeoDesktopStatus>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return NeoDesktopStatus.Failed;
        }
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Cut(string value, int length) => value.Length <= length ? value : value[..(length - 1)].TrimEnd() + "…";
}
