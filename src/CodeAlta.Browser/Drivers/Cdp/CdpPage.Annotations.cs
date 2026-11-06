using System.Collections.Concurrent;
using CodeAlta.Browser.Annotations;
using CodeAlta.Browser.Cdp;
using CodeAlta.Browser.Toolbar;

namespace CodeAlta.Browser.Drivers.Cdp;

/// <summary>The CodeAlta toolbar of a Chromium tab: its script, its binding, and what DevTools knows of an annotated element.</summary>
internal sealed partial class CdpPage : IAnnotatablePage
{
    // The margin around an element in its picture, and the largest picture, in CSS pixels.
    private const double PictureMargin = 16;
    private const double PictureMaximumWidth = 1200;
    private const double PictureMaximumHeight = 900;

    // The stylesheets of the documents of the tab, as the CSS domain announced them: a matched rule names only the id of its sheet.
    private readonly ConcurrentDictionary<string, CSSCSSStyleSheetHeader> _sheets = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<IAsyncDisposable> StartToolbarAsync(Action<ToolbarMessage> onMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        var subscriptions = new List<IDisposable>
        {
            _session.Runtime.OnBindingCalled(item =>
            {
                if (item.Name == ToolbarScript.BindingName && ToolbarMessage.TryParse(item.Payload, item.ExecutionContextId) is { } message) onMessage(message);
            }),
            _session.CSS.OnStyleSheetAdded(item => _sheets[item.Header.StyleSheetId] = item.Header),
            _session.Page.OnFrameNavigated(item =>
            {
                // The sheets of a document go with it.
                if (item.Frame.ParentId is null) _sheets.Clear();
            }),
        };
        try
        {
            // The binding lives in the toolbar's world only: a script of the page cannot report an annotation.
            await _session.Runtime.AddBindingAsync(new RuntimeAddBindingParams { Name = ToolbarScript.BindingName, ExecutionContextName = ToolbarScript.WorldName }, cancellationToken).ConfigureAwait(false);
            await _session.CSS.EnableAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var added = await _session.Page.AddScriptToEvaluateOnNewDocumentAsync(new PageAddScriptToEvaluateOnNewDocumentParams
            {
                Source = ToolbarScript.Source,
                WorldName = ToolbarScript.WorldName,
                RunImmediately = true,
            }, cancellationToken).ConfigureAwait(false);
            return new AnnotationRegistration(_session, added.Identifier, subscriptions);
        }
        catch
        {
            foreach (var subscription in subscriptions) subscription.Dispose();
            throw;
        }
    }

    private sealed class AnnotationRegistration(CdpSession session, string script, List<IDisposable> subscriptions) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var subscription in subscriptions) subscription.Dispose();
            try
            {
                await session.Page.RemoveScriptToEvaluateOnNewDocumentAsync(new PageRemoveScriptToEvaluateOnNewDocumentParams { Identifier = script }).ConfigureAwait(false);
                await session.Runtime.RemoveBindingAsync(new RuntimeRemoveBindingParams { Name = ToolbarScript.BindingName }).ConfigureAwait(false);
            }
            catch (CdpException)
            {
                // The tab is gone.
            }
        }
    }

    /// <inheritdoc />
    public Task RecordNodeCreationAsync(CancellationToken cancellationToken)
        => _session.DOM.SetNodeStackTracesEnabledAsync(new DOMSetNodeStackTracesEnabledParams { Enable = true }, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> CallToolbarAsync(int contextId, string call, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(call);
        try
        {
            var result = await _session.Runtime.EvaluateAsync(new RuntimeEvaluateParams
            {
                Expression = ToolbarScript.Expression(call),
                ContextId = contextId,
                ReturnByValue = true,
                Silent = true,
            }, cancellationToken).ConfigureAwait(false);
            return result.ExceptionDetails is null && result.Result.Value is { ValueKind: System.Text.Json.JsonValueKind.True };
        }
        catch (CdpException)
        {
            // The document of that world is gone.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ElementEvidence> InspectAsync(int contextId, string id, int index, AnnotatedElement element, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(element);
        string? objectId = null;
        try
        {
            var found = await _session.Runtime.EvaluateAsync(new RuntimeEvaluateParams
            {
                Expression = ToolbarScript.Expression($"node({Json(id)}, {index.ToString(System.Globalization.CultureInfo.InvariantCulture)})"),
                ContextId = contextId,
                Silent = true,
            }, cancellationToken).ConfigureAwait(false);
            objectId = found.Result.ObjectId;
            if (objectId is null) return ElementEvidence.None;

            // A node is known to the DOM domain once its document was asked for.
            await _session.DOM.GetDocumentAsync(new DOMGetDocumentParams { Depth = 0 }, cancellationToken).ConfigureAwait(false);
            var node = await _session.DOM.RequestNodeAsync(new DOMRequestNodeParams { ObjectId = objectId }, cancellationToken).ConfigureAwait(false);
            var rules = await RulesAsync(node.NodeId, cancellationToken).ConfigureAwait(false);
            var creation = await CreationAsync(node.NodeId, cancellationToken).ConfigureAwait(false);
            return new ElementEvidence(rules, creation);
        }
        catch (CdpException)
        {
            // The element or its document went away meanwhile.
            return ElementEvidence.None;
        }
        finally
        {
            if (objectId is not null)
            {
                try { await _session.Runtime.ReleaseObjectAsync(new RuntimeReleaseObjectParams { ObjectId = objectId }, CancellationToken.None).ConfigureAwait(false); }
                catch (CdpException) { /* gone with its document */ }
            }
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> PictureAsync(AnnotationArea area, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (area.Width <= 0 || area.Height <= 0) return null;
        // A rectangle the user drew is the picture; elements they chose are shown with some of what is around them.
        var margin = area.Drawn ? 0 : PictureMargin;
        var x = Math.Max(0, area.X - margin);
        var y = Math.Max(0, area.Y - margin);
        var width = Math.Min(area.Width + 2 * margin, PictureMaximumWidth);
        var height = Math.Min(area.Height + 2 * margin, PictureMaximumHeight);
        try
        {
            return await ScreenshotAsync(new ScreenshotRequest { Clip = (x, y, width, height) }, cancellationToken).ConfigureAwait(false);
        }
        catch (CdpException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> ViewPictureAsync(AnnotationArea area, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (area.Width <= 0 || area.Height <= 0) return null;
        try
        {
            // The cursor and the glow of the agent are no part of what the user drew over the page.
            await using var overlay = await HideAgentOverlayAsync(cancellationToken).ConfigureAwait(false);
            // Not beyond the viewport: a capture of the whole document lays the page out again in a taller viewport,
            // and what the toolbar draws, fixed to the viewport, would no longer be over what it was drawn on.
            var shot = await _session.Page.CaptureScreenshotAsync(new PageCaptureScreenshotParams
            {
                Format = "png",
                Clip = new PageViewport { X = area.X, Y = area.Y, Width = Math.Min(area.Width, PictureMaximumWidth), Height = Math.Min(area.Height, PictureMaximumHeight), Scale = 1 },
                CaptureBeyondViewport = false,
            }, cancellationToken).ConfigureAwait(false);
            return Convert.FromBase64String(shot.Data);
        }
        catch (CdpException)
        {
            return null;
        }
    }

    // The rules the authors of the page wrote (not the browser's own), the most specific first.
    private async Task<IReadOnlyList<StyleRuleEvidence>> RulesAsync(int nodeId, CancellationToken cancellationToken)
    {
        CSSGetMatchedStylesForNodeResult matched;
        try
        {
            matched = await _session.CSS.GetMatchedStylesForNodeAsync(new CSSGetMatchedStylesForNodeParams { NodeId = nodeId }, cancellationToken).ConfigureAwait(false);
        }
        catch (CdpException)
        {
            return [];
        }

        var rules = new List<StyleRuleEvidence>();
        foreach (var match in (matched.MatchedCSSRules ?? []).Reverse())
        {
            var rule = match.Rule;
            if (rule.Origin != "regular" || rule.StyleSheetId is not { } sheetId) continue;
            _sheets.TryGetValue(sheetId, out var sheet);
            var owner = sheet?.OwnerNode is { } backendNodeId ? await AttributesAsync(backendNodeId, cancellationToken).ConfigureAwait(false) : [];
            var range = rule.Style.Range;
            rules.Add(new StyleRuleEvidence(rule.SelectorList.Text, sheet?.SourceURL, sheet?.SourceMapURL, sheet?.IsInline ?? false,
                range?.StartLine ?? -1, range?.StartColumn ?? 0, (int)(sheet?.StartLine ?? 0), owner));
            if (rules.Count == 6) break;
        }

        return rules;
    }

    private async Task<IReadOnlyList<KeyValuePair<string, string>>> AttributesAsync(int backendNodeId, CancellationToken cancellationToken)
    {
        try
        {
            var described = await _session.DOM.DescribeNodeAsync(new DOMDescribeNodeParams { BackendNodeId = backendNodeId }, cancellationToken).ConfigureAwait(false);
            var attributes = described.Node.Attributes ?? [];
            var pairs = new List<KeyValuePair<string, string>>();
            for (var index = 0; index + 1 < attributes.Length; index += 2) pairs.Add(new(attributes[index], attributes[index + 1]));
            return pairs;
        }
        catch (CdpException)
        {
            return [];
        }
    }

    // The frames of the script that created the node, and of the scripts that scheduled it, innermost first.
    private async Task<IReadOnlyList<StackFrameEvidence>> CreationAsync(int nodeId, CancellationToken cancellationToken)
    {
        DOMGetNodeStackTracesResult traces;
        try
        {
            traces = await _session.DOM.GetNodeStackTracesAsync(new DOMGetNodeStackTracesParams { NodeId = nodeId }, cancellationToken).ConfigureAwait(false);
        }
        catch (CdpException)
        {
            return [];
        }

        var frames = new List<StackFrameEvidence>();
        for (var stack = traces.Creation; stack is not null && frames.Count < 40; stack = stack.Parent)
        {
            foreach (var frame in stack.CallFrames)
            {
                if (string.IsNullOrEmpty(frame.Url)) continue;
                frames.Add(new StackFrameEvidence(frame.FunctionName, frame.Url, frame.LineNumber, frame.ColumnNumber));
                if (frames.Count == 40) break;
            }
        }

        return frames;
    }
}
