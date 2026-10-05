using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tui.Plugins;

/// <summary>
/// What the terminal application gives <see cref="TerminalPluginUi"/> once its window exists. Every member
/// is called on the UI thread.
/// </summary>
internal interface ITerminalPluginUiHost
{
    /// <summary>Gets the dispatcher of the UI thread.</summary>
    IUiDispatcher Dispatcher { get; }

    /// <summary>Gets the area a dialog is sized for, or null for the whole application.</summary>
    Rectangle? GetDialogBounds();

    /// <summary>Gets the control that takes the focus back when a dialog closes.</summary>
    Visual? GetFocusTarget();

    /// <summary>Shows a short message to the user.</summary>
    void ShowMessage(string message, bool warning);

    /// <summary>Gets the selected session, or null.</summary>
    string? SelectedSessionId { get; }

    /// <summary>Gets a value indicating whether the selected session is running.</summary>
    bool IsSelectedSessionBusy { get; }

    /// <summary>Gets or sets the text of the prompt.</summary>
    string? PromptText { get; set; }

    /// <summary>Sends a prompt to the selected session, or steers its running turn.</summary>
    Task SendPromptAsync(string text, bool steer);

    /// <summary>Queues a prompt for the selected session.</summary>
    /// <returns>False when no session is selected.</returns>
    bool EnqueuePrompt(string text);

    /// <summary>Compacts the selected session.</summary>
    /// <returns>False when no session is selected.</returns>
    Task<bool> CompactAsync();
}

/// <summary>
/// The dialogs, the selected session and the prompt that the terminal application gives its plugins.
/// </summary>
/// <remarks>
/// Plugins start before the application has a window, so this object exists first and the window is
/// attached later. Until then it answers like a host without a user interface: nothing is shown and a
/// question has no answer.
/// </remarks>
internal sealed class TerminalPluginUi : IPluginUiService, IPluginSessionService, IPluginPromptService
{
    private volatile ITerminalPluginUiHost? _host;

    /// <summary>Gives the window to the plugins.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    internal void Attach(ITerminalPluginUiHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>Takes the window back, when the application closes.</summary>
    internal void Detach() => _host = null;

    /// <inheritdoc />
    public bool HasInteractiveUi => _host is not null;

    /// <inheritdoc />
    public ValueTask NotifyAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is { } host) host.Dispatcher.Post(() => host.ShowMessage(message, warning: false));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);
        var response = await ShowAsync(new PluginRequestDialogModel
        {
            Kind = PluginDialogKind.Confirmation, Title = title, Message = message,
            Buttons = [new() { Name = "no", Label = SR.T("No"), IsCancel = true }, new() { Name = "yes", Label = SR.T("Yes"), IsDefault = true }],
        }, cancellationToken).ConfigureAwait(false);
        return response is { Cancelled: false, ButtonName: "yes" };
    }

    /// <inheritdoc />
    public async ValueTask<string?> InputAsync(string title, string? initialText = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var response = await ShowAsync(new PluginRequestDialogModel { Kind = PluginDialogKind.Input, Title = title, Text = initialText, Buttons = OkCancel() }, cancellationToken)
            .ConfigureAwait(false);
        return response is { Cancelled: false } ? response.Text ?? string.Empty : null;
    }

    /// <inheritdoc />
    public async ValueTask<string?> EditTextAsync(string title, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(text);
        var response = await ShowAsync(new PluginRequestDialogModel { Kind = PluginDialogKind.TextEditor, Title = title, Text = text, Buttons = OkCancel() }, cancellationToken)
            .ConfigureAwait(false);
        return response is { Cancelled: false } ? response.Text ?? string.Empty : null;
    }

    /// <inheritdoc />
    public async ValueTask<T?> SelectAsync<T>(string title, IReadOnlyList<PluginSelectItem<T>> items, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return default;
        var selected = 0;
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].IsSelected) { selected = index; break; }
        }

        var response = await ShowAsync(new PluginRequestDialogModel
        {
            Kind = PluginDialogKind.Selection, Title = title, SelectedIndex = selected, Buttons = OkCancel(),
            Choices = [.. items.Select(static item => new PluginRequestDialogChoice(item.Label, item.Description))],
        }, cancellationToken).ConfigureAwait(false);
        return response is { Cancelled: false, SelectedIndex: { } chosen } && chosen >= 0 && chosen < items.Count ? items[chosen].Value : default;
    }

    /// <inheritdoc />
    public async ValueTask ShowDialogAsync(PluginDialogRequest request, CancellationToken cancellationToken = default)
        => _ = await ShowDialogForResultAsync(request, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<PluginDialogResponse?> ShowDialogForResultAsync(PluginDialogRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        return CreateModel(request) is { } model ? ShowAsync(model, cancellationToken) : new((PluginDialogResponse?)null);
    }

    /// <summary>
    /// Decides what a request shows in the terminal. A custom request shows its native content; one that
    /// has only an HTML fragment shows its message, and nothing when it has none.
    /// </summary>
    /// <returns>The dialog to show, or null when the request has nothing the terminal can show.</returns>
    internal static PluginRequestDialogModel? CreateModel(PluginDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = (request as PluginTerminalDialogRequest)?.Content;
        var kind = request.Kind == PluginDialogKind.Custom && content is null ? PluginDialogKind.Notification : request.Kind;
        if (kind == PluginDialogKind.Notification && string.IsNullOrWhiteSpace(request.Message)) return null;
        return new PluginRequestDialogModel
        {
            Kind = kind, Title = request.Title, Message = request.Message, Text = request.InitialText, Content = content,
            Choices = kind == PluginDialogKind.Selection ? [.. request.SelectionItems.Select(static label => new PluginRequestDialogChoice(label ?? string.Empty, null))] : [],
            Buttons = request.Buttons.Count > 0 ? request.Buttons
                : kind is PluginDialogKind.Notification or PluginDialogKind.Custom ? [new() { Name = "close", Label = SR.T("Close"), IsDefault = true, IsCancel = true }]
                : kind == PluginDialogKind.Confirmation ? [new() { Name = "no", Label = SR.T("No"), IsCancel = true }, new() { Name = "yes", Label = SR.T("Yes"), IsDefault = true }]
                : OkCancel(),
        };
    }

    /// <inheritdoc />
    public string? SelectedSessionId => Read(static host => host.SelectedSessionId);

    /// <inheritdoc />
    public bool IsSelectedSessionBusy => Read(static host => host.IsSelectedSessionBusy);

    /// <inheritdoc />
    public async ValueTask SendPromptAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is { } host) await UiDispatch.InvokeAsync(host.Dispatcher, () => host.SendPromptAsync(text, steer: false), allowInline: true).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask EnqueuePromptAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is not { } host) return;
        // Without a session there is no queue: the prompt starts one, like a prompt the user sends.
        await UiDispatch.InvokeAsync(host.Dispatcher, () => host.EnqueuePrompt(text) ? Task.CompletedTask : host.SendPromptAsync(text, steer: false), allowInline: true)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TrySteerAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is not { } host) return false;
        var steered = false;
        await UiDispatch.InvokeAsync(host.Dispatcher, async () =>
        {
            if (host.SelectedSessionId is null || !host.IsSelectedSessionBusy) return;
            await host.SendPromptAsync(text, steer: true);
            steered = true;
        }, allowInline: true).ConfigureAwait(false);
        return steered;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RequestCompactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is not { } host) return false;
        var compacted = false;
        await UiDispatch.InvokeAsync(host.Dispatcher, async () => compacted = await host.CompactAsync(), allowInline: true).ConfigureAwait(false);
        return compacted;
    }

    /// <inheritdoc />
    public string? DraftText => Read(static host => host.PromptText);

    /// <inheritdoc />
    public async ValueTask SetDraftTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is { } host) await host.Dispatcher.InvokeAsync(() => host.PromptText = text).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask AddAttachmentAsync(PluginPromptAttachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PluginPromptAttachment>> GetAttachmentsAsync(CancellationToken cancellationToken = default)
        => new(Array.Empty<PluginPromptAttachment>());

    private T? Read<T>(Func<ITerminalPluginUiHost, T?> read)
        => _host is { } host ? host.Dispatcher.Invoke(() => read(host)) : default;

    private async ValueTask<PluginDialogResponse?> ShowAsync(PluginRequestDialogModel model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is not { } host) return null;
        var answer = new TaskCompletionSource<PluginDialogResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PluginRequestDialog? dialog = null;
        await host.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                dialog = new PluginRequestDialog(model, host.GetDialogBounds, host.GetFocusTarget, response => answer.TrySetResult(response));
                dialog.Show();
            }
            catch (InvalidOperationException)
            {
                // No application is running any more: the question has no answer.
                dialog = null;
                answer.TrySetResult(null);
            }
        }).ConfigureAwait(false);

        await using var registration = cancellationToken.Register(() => host.Dispatcher.Post(() =>
        {
            dialog?.Close();
            answer.TrySetCanceled(cancellationToken);
        })).ConfigureAwait(false);
        return await answer.Task.ConfigureAwait(false);
    }

    private static PluginDialogButton[] OkCancel()
        => [new() { Name = "cancel", Label = SR.T("Cancel"), IsCancel = true }, new() { Name = "ok", Label = SR.T("OK"), IsDefault = true }];
}
