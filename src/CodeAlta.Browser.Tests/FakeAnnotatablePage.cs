using System.Collections.Concurrent;
using System.Text.Json;
using CodeAlta.Browser.Annotations;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Toolbar;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Browser.Tests;

/// <summary>
/// A tab with the CodeAlta toolbar and no browser behind it: a test says what its toolbar reports, in the order a page
/// would, and when the pictures of its annotations come.
/// </summary>
internal sealed class FakeAnnotatablePage(string url) : IBrowserPage, IAnnotatablePage
{
    /// <summary>The document the toolbar reports from.</summary>
    public const int Context = 1;

    private readonly TaskCompletionSource<Action<ToolbarMessage>> _toolbar = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets the calls made to the toolbar, in their order.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Gets or sets what gives the picture of an annotation or a drawing; none by default.</summary>
    public Func<AnnotationArea, CancellationToken, Task<byte[]?>> Picture { get; set; } = static (_, _) => Task.FromResult<byte[]?>(null);

    /// <summary>Reports a message of the toolbar, as the toolbar of the page sends it, once the toolbar was started.</summary>
    public async Task ReportAsync(string json)
    {
        var onMessage = await _toolbar.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        onMessage(ToolbarMessage.TryParse(json, Context) ?? throw new ArgumentException("The toolbar never sends this message: " + json, nameof(json)));
    }

    /// <summary>Reports that the toolbar of the document is ready, and waits until the host enabled it.</summary>
    public async Task ReadyAsync()
    {
        await ReportAsync($$"""{"type":"ready","url":{{JsonSerializer.Serialize(Url)}}}""").ConfigureAwait(false);
        await WaitForCallAsync("enable(").ConfigureAwait(false);
    }

    /// <summary>Waits until the toolbar was called with a call that starts with a text.</summary>
    public async Task WaitForCallAsync(string start)
    {
        for (var deadline = DateTime.UtcNow.AddSeconds(10); !Calls.Any(call => call.StartsWith(start, StringComparison.Ordinal)); await Task.Delay(20).ConfigureAwait(false))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The toolbar was never called with {start}: {string.Join(", ", Calls)}");
        }
    }

    public string Url { get; } = url;

    public string Title => "Test page";

    public bool IsClosed => false;

    public BrowserFrame MainFrame { get; } = new("main");

    public PageLog Log { get; } = new();

    public PageDialog? Dialog => null;

    public long Navigations => 1;

    public Task<IAsyncDisposable> StartToolbarAsync(Action<ToolbarMessage> onMessage, CancellationToken cancellationToken)
    {
        _toolbar.TrySetResult(onMessage);
        return Task.FromResult<IAsyncDisposable>(new Registration());
    }

    public Task RecordNodeCreationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> CallToolbarAsync(int contextId, string call, CancellationToken cancellationToken)
    {
        Calls.Enqueue(call);
        return Task.FromResult(contextId == Context);
    }

    public Task<ElementEvidence> InspectAsync(int contextId, string id, int index, AnnotatedElement element, CancellationToken cancellationToken) => Task.FromResult(ElementEvidence.None);

    public Task<byte[]?> PictureAsync(AnnotationArea area, CancellationToken cancellationToken) => Picture(area, cancellationToken);

    public Task<byte[]?> ViewPictureAsync(AnnotationArea area, CancellationToken cancellationToken) => Picture(area, cancellationToken);

    public Task NavigateAsync(string url, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task TraverseAsync(int delta, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ReloadAsync(bool ignoreCache, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> WaitForNavigationAsync(long navigationsBefore, TimeSpan startWindow, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<JsonElement> CallAgentAsync(BrowserFrame frame, string operation, string optionsJson, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<BrowserFrame?> ChildFrameAsync(BrowserFrame parent, string uid, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string?> EvaluateAsync(string source, bool isFunction, IReadOnlyList<(BrowserFrame Frame, string Uid)> arguments, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IAsyncDisposable> AddInitScriptAsync(string source, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetFilesAsync(BrowserFrame frame, string uid, IReadOnlyList<string> paths, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task MouseMoveAsync(BrowserPoint point, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ClickAsync(BrowserPoint point, int clickCount, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DragAsync(BrowserPoint from, BrowserPoint to, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task KeyAsync(BrowserKey key, bool down, int modifiers, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task InsertTextAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<byte[]> ScreenshotAsync(ScreenshotRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task HandleDialogAsync(bool accept, string? promptText, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IDisposable AnswerDialogs(Func<PageDialog, (bool Accept, string? Text)?> policy) => throw new NotSupportedException();

    public Task<NetworkBodies> ReadBodiesAsync(NetworkEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<(int Width, int Height)?> ResizeAsync(int width, int height, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task EmulateAsync(EmulationSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task BringToFrontAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CloseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    private sealed class Registration : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
