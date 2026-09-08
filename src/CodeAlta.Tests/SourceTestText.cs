using System.Text;

namespace CodeAlta.Tests;

/// <summary>Canonical source content only; no filesystem access or production execution.</summary>
internal static class SourceTestText
{
    // Fragments need not have a final newline. No whitespace except CR in CRLF is changed.
    internal static string Canonicalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.StartsWith('\uFEFF'))
        {
            throw new ArgumentException("Source text must not have a BOM.", nameof(text));
        }

        var canonical = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (canonical.Contains('\r'))
        {
            throw new ArgumentException("Source text must not contain a lone CR.", nameof(text));
        }

        return canonical;
    }

    // Decode actual checkout bytes strictly before comparing canonical content.
    internal static string DecodeSource(ReadOnlySpan<byte> bytes)
    {
        var canonical = Canonicalize(new UTF8Encoding(false, true).GetString(bytes));
        if (!canonical.EndsWith('\n'))
        {
            throw new ArgumentException("Source text must have a final newline.", nameof(bytes));
        }

        return canonical;
    }

    // These are explicit historical bytes, never a claim about actual checkout representation.
    internal static byte[] HistoricalBytes(string text, bool crLf)
    {
        var canonical = Canonicalize(text);
        return new UTF8Encoding(false, true).GetBytes(
            crLf ? canonical.Replace("\n", "\r\n", StringComparison.Ordinal) : canonical);
    }
}
