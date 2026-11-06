using System.Text.Json;
using CodeAlta.Browser.Agent;
using CodeAlta.Browser.Cdp;

namespace CodeAlta.Browser.Drivers.Cdp;

/// <summary>
/// The overlay that shows the user where the agent acts in a Chromium tab with a window: the cursor moves with the pointer
/// input of the tools, the glow and the label show while they act, and all of it hides while the tab takes a picture.
/// </summary>
internal sealed partial class CdpPage : IAgentActivityPage
{
    // The world of the overlay in the document of the tab, 0 while it has none: a new document has none, and the call that
    // finds the world of the previous one gone makes another.
    private int _overlayWorld;

    /// <inheritdoc />
    public bool ShowsAgent => _owner.Options.ShowAgent;

    /// <inheritdoc />
    public async Task ShowAgentActivityAsync(CancellationToken cancellationToken)
    {
        if (!ShowsAgent || Dialog is not null) return;
        await CallOverlayAsync($"activity({Milliseconds(AgentOverlayScript.ActivityDuration)})", create: true, AgentOverlayScript.CallLimit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The state of the overlay of the document, as the tests read it, or null when it has none.</summary>
    internal Task<JsonElement?> AgentOverlayStateAsync(CancellationToken cancellationToken)
        => CallOverlayAsync("state()", create: false, AgentOverlayScript.CallLimit, cancellationToken);

    // Moves the cursor to where the input of the agent goes. A move waits until the user saw the cursor arrive, within
    // the limit; the steps of a drag follow at once, as does a cursor already there.
    private async Task PointAgentAsync(double x, double y, bool wait, CancellationToken cancellationToken)
    {
        if (!ShowsAgent || Dialog is not null) return;
        var options = $$"""{"wait":{{(wait ? "true" : "false")}},"duration":{{Milliseconds(AgentOverlayScript.ActivityDuration)}}}""";
        await CallOverlayAsync($"move({FormatNumber(x)}, {FormatNumber(y)}, {options})", create: true,
            wait ? AgentOverlayScript.ArrivalLimit : AgentOverlayScript.CallLimit, cancellationToken).ConfigureAwait(false);
    }

    // A ring where the agent presses; the press never waits for it.
    private async Task PressAgentAsync(CancellationToken cancellationToken)
    {
        if (!ShowsAgent || Dialog is not null) return;
        await CallOverlayAsync("press()", create: false, AgentOverlayScript.CallLimit, cancellationToken).ConfigureAwait(false);
    }

    // Hides the overlay while a picture of the tab is taken, by the agent or for an annotation: what the agent sees of the
    // page is the page alone. Disposing the result shows it again.
    private async Task<OverlayHidden> HideAgentOverlayAsync(CancellationToken cancellationToken)
    {
        if (!ShowsAgent || Volatile.Read(ref _overlayWorld) == 0 || Dialog is not null) return default;
        var hidden = await CallOverlayAsync("hide()", create: false, AgentOverlayScript.CallLimit, cancellationToken).ConfigureAwait(false);
        return new OverlayHidden(hidden is { ValueKind: JsonValueKind.True } ? this : null);
    }

    private readonly struct OverlayHidden(CdpPage? page) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (page is not null) await page.CallOverlayAsync("show()", create: false, AgentOverlayScript.CallLimit, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Calls the overlay of the document of the tab, making it first when asked. The overlay only shows what the agent does:
    // a call that fails, or that the page is too busy to answer within its limit, is given up, never the action with it.
    private async Task<JsonElement?> CallOverlayAsync(string call, bool create, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(limit);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var world = Volatile.Read(ref _overlayWorld);
            try
            {
                if (world == 0)
                {
                    if (!create) return null;
                    world = await OverlayWorldAsync(source.Token).ConfigureAwait(false);
                }

                var result = await _session.Runtime.EvaluateAsync(new RuntimeEvaluateParams
                {
                    Expression = AgentOverlayScript.Expression(call),
                    ContextId = world,
                    ReturnByValue = true,
                    AwaitPromise = true,
                    Silent = true,
                }, source.Token).ConfigureAwait(false);
                if (result.ExceptionDetails is not null) return null;
                if (result.Result.Value is { ValueKind: not JsonValueKind.Null } value) return value.Clone();
                // A world without the overlay: a document that refused it, as a frame that is not the top one.
                return null;
            }
            catch (CdpException)
            {
                // The document of that world is gone: the next attempt makes the world of the new one.
                Interlocked.CompareExchange(ref _overlayWorld, 0, world);
                if (!create) return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<int> OverlayWorldAsync(CancellationToken cancellationToken)
    {
        var world = await _session.Page.CreateIsolatedWorldAsync(new PageCreateIsolatedWorldParams
        {
            FrameId = Volatile.Read(ref _mainFrameId),
            WorldName = AgentOverlayScript.WorldName,
            GrantUniveralAccess = false,
        }, cancellationToken).ConfigureAwait(false);
        await _session.Runtime.EvaluateAsync(new RuntimeEvaluateParams { Expression = AgentOverlayScript.Source, ContextId = world.ExecutionContextId, Silent = true }, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _overlayWorld, world.ExecutionContextId);
        return world.ExecutionContextId;
    }

    private static string Milliseconds(TimeSpan span) => FormatNumber(Math.Round(span.TotalMilliseconds));
}
