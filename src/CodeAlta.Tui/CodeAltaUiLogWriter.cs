using XenoAtom.Logging.Writers;

namespace CodeAlta.Tui;

internal sealed class CodeAltaUiLogWriter : TerminalLogWriterBase
{
    private readonly CodeAltaUiLogBuffer _buffer;

    public CodeAltaUiLogWriter(CodeAltaUiLogBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
    }

    protected override void AppendLine(scoped ReadOnlySpan<char> text)
        => _buffer.Append(text.ToString(), isMarkup: false);

    protected override void AppendMarkupLine(scoped ReadOnlySpan<char> markupText)
        => _buffer.Append(markupText.ToString(), isMarkup: true);
}
