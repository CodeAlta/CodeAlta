using CodeAlta.Catalog;
using CodeAlta.Presentation.Controls;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Sessions;

internal sealed class SessionInfoPresenter
{
    private const int PopupMinWidth = 56;
    private const int PopupMaxWidth = 84;

    private readonly Action<string> _copyMarkdown;
    private readonly Func<CancellationToken, Task<SessionInfoReport?>> _loadReportAsync;
    private readonly Action<Action> _dispatchToUi;
    private readonly Func<Func<Visual>, Visual> _createComputedVisual;
    private readonly Action _focusPromptEditor;
    private readonly State<int> _refreshState = new(0);
    private AnchoredPopupView? _popupView;
    private CancellationTokenSource? _loadCancellationTokenSource;
    private SessionInfoReport? _report;
    private bool _isLoading;
    private string? _errorMessage;

    public SessionInfoPresenter(
        Action<string> copyMarkdown,
        Func<CancellationToken, Task<SessionInfoReport?>> loadReportAsync,
        Action<Action> dispatchToUi,
        Func<Func<Visual>, Visual> createComputedVisual,
        Action focusPromptEditor)
    {
        ArgumentNullException.ThrowIfNull(copyMarkdown);
        ArgumentNullException.ThrowIfNull(loadReportAsync);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(createComputedVisual);
        ArgumentNullException.ThrowIfNull(focusPromptEditor);

        _copyMarkdown = copyMarkdown;
        _loadReportAsync = loadReportAsync;
        _dispatchToUi = dispatchToUi;
        _createComputedVisual = createComputedVisual;
        _focusPromptEditor = focusPromptEditor;
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
        _popupView ??= new AnchoredPopupView(() => _createComputedVisual(BuildPopupContent), _focusPromptEditor);
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
            var report = await _loadReportAsync(cancellationToken);
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

        var copyButton = new Button(new TextBlock($"{TerminalIcons.MdContentCopy}"))
            .Click(CopyMarkdown);
        var copyButtonHost = copyButton.Tooltip(new TextBlock(SR.T("Copy this report as markdown.")));
        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose}"))
        {
            Tone = ControlTone.Error,
        };
        closeButton.Click(ClosePopup);
        var closeButtonHost = closeButton.Tooltip(new TextBlock(SR.T("Close the session info popup.")));

        var content = new VStack
        {
            Spacing = 1,
        };
        content.Add(new StatusBar()
            .LeftText(new VStack(
                new Markup($"[bold]{SR.T("Session info")}[/]"),
                new Markup($"[dim]{AnsiMarkup.Escape(SessionInfoFormatter.BuildSubtitle(_report, _isLoading))}[/]"))
            {
                Spacing = 0,
            })
            .RightText(new HStack(copyButtonHost, closeButtonHost)
            {
                Spacing = 1,
            }));

        content.Add(new MarkdownControl(SessionInfoFormatter.BuildBodyMarkdown(_report, _isLoading, _errorMessage))
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

        _copyMarkdown(SessionInfoFormatter.BuildMarkdown(_report));
    }
}
