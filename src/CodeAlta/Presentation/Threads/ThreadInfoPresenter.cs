using CodeAlta.Presentation.Controls;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Threads;

internal sealed class ThreadInfoPresenter
{
    private const int PopupMinWidth = 56;
    private const int PopupMaxWidth = 84;

    private readonly Action<string> _copyMarkdown;
    private readonly Func<CancellationToken, Task<ThreadInfoReport?>> _loadReportAsync;
    private readonly Action<Action> _dispatchToUi;
    private readonly Func<Func<Visual>, Visual> _createComputedVisual;
    private readonly State<int> _refreshState = new(0);
    private AnchoredPopupView? _popupView;
    private CancellationTokenSource? _loadCancellationTokenSource;
    private ThreadInfoReport? _report;
    private bool _isLoading;
    private string? _errorMessage;

    public ThreadInfoPresenter(
        Action<string> copyMarkdown,
        Func<CancellationToken, Task<ThreadInfoReport?>> loadReportAsync,
        Action<Action> dispatchToUi,
        Func<Func<Visual>, Visual> createComputedVisual)
    {
        ArgumentNullException.ThrowIfNull(copyMarkdown);
        ArgumentNullException.ThrowIfNull(loadReportAsync);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(createComputedVisual);

        _copyMarkdown = copyMarkdown;
        _loadReportAsync = loadReportAsync;
        _dispatchToUi = dispatchToUi;
        _createComputedVisual = createComputedVisual;
    }

    public void TogglePopup(Visual anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        if (_popupView?.IsOpen == true)
        {
            ClosePopup();
            return;
        }

        ShowPopup(anchor);
    }

    public void ClosePopup()
    {
        CancelLoad();
        _popupView?.Close();
    }

    public void InvalidateSelection()
    {
        CancelLoad();
        _dispatchToUi(() =>
        {
            _report = null;
            _isLoading = false;
            _errorMessage = null;
            _refreshState.Value++;
            _popupView?.Close();
        });
    }

    private void ShowPopup(Visual anchor)
    {
        _popupView ??= new AnchoredPopupView(() => _createComputedVisual(BuildPopupContent));
        _popupView.Show(anchor);
        StartLoad();
    }

    private void StartLoad()
    {
        CancelLoad();
        _isLoading = true;
        _errorMessage = null;
        _refreshState.Value++;

        var cancellationTokenSource = new CancellationTokenSource();
        _loadCancellationTokenSource = cancellationTokenSource;
        _ = LoadAsync(cancellationTokenSource.Token);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await _loadReportAsync(cancellationToken).ConfigureAwait(false);
            _dispatchToUi(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _report = report;
                _isLoading = false;
                _errorMessage = null;
                _refreshState.Value++;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _dispatchToUi(() =>
            {
                _report = null;
                _isLoading = false;
                _errorMessage = ex.Message;
                _refreshState.Value++;
            });
        }
    }

    private void CancelLoad()
    {
        _loadCancellationTokenSource?.Cancel();
        _loadCancellationTokenSource?.Dispose();
        _loadCancellationTokenSource = null;
    }

    private Visual BuildPopupContent()
    {
        var _ = _refreshState.Value;

        var copyButton = new Button(new TextBlock($"{NerdFont.MdContentCopy}"))
            .Click(CopyMarkdown);
        var copyButtonHost = copyButton.Tooltip(new TextBlock("Copy this report as markdown."));
        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose}"))
        {
            Tone = ControlTone.Error,
        };
        closeButton.Click(ClosePopup);
        var closeButtonHost = closeButton.Tooltip(new TextBlock("Close the thread info popup."));

        var content = new VStack
        {
            Spacing = 1,
        };
        content.Add(new StatusBar()
            .LeftText(new VStack(
                new Markup("[bold]Thread info[/]"),
                new Markup($"[dim]{AnsiMarkup.Escape(ThreadInfoFormatter.BuildSubtitle(_report, _isLoading))}[/]"))
            {
                Spacing = 0,
            })
            .RightText(new HStack(copyButtonHost, closeButtonHost)
            {
                Spacing = 1,
            }));

        content.Add(new MarkdownControl(ThreadInfoFormatter.BuildBodyMarkdown(_report, _isLoading, _errorMessage))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
            Options = MarkdownRenderOptions.Default with
            {
                WrapCodeBlocks = true,
                MaxCodeBlockHeight = 12,
            },
        });

        return new Padder(content)
        {
            Padding = new Thickness(1),
            MinWidth = PopupMinWidth,
            MaxWidth = PopupMaxWidth,
        };
    }

    private void CopyMarkdown()
    {
        if (_report is null)
        {
            return;
        }

        _copyMarkdown(ThreadInfoFormatter.BuildMarkdown(_report));
    }
}
