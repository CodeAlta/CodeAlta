using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Browser.Annotations;
using CodeAlta.Browser.Cdp;
using CodeAlta.Browser.Drivers;

namespace CodeAlta.Browser.Tests;

/// <summary>Waits for observable browser state, without delaying a browser that is already ready.</summary>
internal static class BrowserWait
{
    internal static Task PageAsync(IBrowserPage page, string condition, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => UntilAsync(async token => await page.EvaluateAsync(condition, isFunction: false, [], token) == "true", condition, cancellationToken, file, line);

    internal static Task ToolbarAsync(IAnnotatablePage page, int contextId, string condition, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => UntilAsync(token => page.CallToolbarAsync(contextId, condition, token), condition, cancellationToken, file, line);

    internal static Task EditorAsync(IBrowserPage page, CancellationToken cancellationToken = default, string selector = "textarea",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => ToolbarRootAsync(page, $"this.activeElement?.matches('{selector}') === true", cancellationToken, file, line);

    internal static Task ToolbarRootAsync(IBrowserPage page, string condition, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => UntilAsync(token => ToolbarRootMatchesAsync(page, condition, token), condition, cancellationToken, file, line);

    internal static async Task UntilAsync(Func<CancellationToken, Task<bool>> ready, string condition, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var limit = TestTime.Seconds(10);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            while (!await ready(timeout.Token))
                await Task.Delay(20, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(TestTime.RanOut(limit, condition, file, line));
        }
    }

    // The page sees focus on the host even when it is still on a toolbar button. DevTools sees which control in the
    // closed shadow root has focus, so keyboard input can go to the editor only once its deferred focus has arrived.
    private static async Task<bool> ToolbarRootMatchesAsync(IBrowserPage page, string condition, CancellationToken cancellationToken)
    {
        var session = BrowserAnnotationsTests.CdpSessionOf(page);
        var host = await session.Runtime.EvaluateAsync(new RuntimeEvaluateParams
        {
            Expression = "document.activeElement?.hasAttribute('data-codealta-toolbar') ? document.activeElement : null",
        }, cancellationToken);
        if (host.Result.ObjectId is not { } hostId) return false;

        string? rootId = null;
        try
        {
            var described = await session.DOM.DescribeNodeAsync(new DOMDescribeNodeParams { ObjectId = hostId, Depth = 1, Pierce = true }, cancellationToken);
            if (described.Node.ShadowRoots?.SingleOrDefault() is not { } root) return false;
            var resolved = await session.DOM.ResolveNodeAsync(new DOMResolveNodeParams { BackendNodeId = root.BackendNodeId }, cancellationToken);
            rootId = resolved.Object.ObjectId;
            var result = await session.Runtime.CallFunctionOnAsync(new RuntimeCallFunctionOnParams
            {
                ObjectId = rootId, ReturnByValue = true, FunctionDeclaration = $"function () {{ return {condition}; }}",
            }, cancellationToken);
            Assert.IsNull(result.ExceptionDetails, condition);
            return result.Result.Value is { ValueKind: JsonValueKind.True };
        }
        finally
        {
            if (rootId is not null) await session.Runtime.ReleaseObjectAsync(new RuntimeReleaseObjectParams { ObjectId = rootId }, cancellationToken);
            await session.Runtime.ReleaseObjectAsync(new RuntimeReleaseObjectParams { ObjectId = hostId }, cancellationToken);
        }
    }
}
