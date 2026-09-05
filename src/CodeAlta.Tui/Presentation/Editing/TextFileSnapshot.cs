using System.Text;

namespace CodeAlta.Tui.Presentation.Editing;

internal sealed record TextFileSnapshot(
    string Text,
    Encoding Encoding,
    bool HasByteOrderMark,
    DateTimeOffset LastWriteTimeUtc);
