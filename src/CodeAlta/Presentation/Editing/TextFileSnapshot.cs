using System.Text;

namespace CodeAlta.Presentation.Editing;

internal sealed record TextFileSnapshot(
    string Text,
    Encoding Encoding,
    bool HasByteOrderMark,
    DateTimeOffset LastWriteTimeUtc);
