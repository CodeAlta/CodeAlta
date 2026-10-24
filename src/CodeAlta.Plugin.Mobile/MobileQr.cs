using QRCoder;

namespace CodeAlta.Plugin.Mobile;

/// <summary>The QR code of the address a phone opens, as the Mobile tab draws it.</summary>
internal static class MobileQr
{
    /// <summary>Encodes a text as the modules of a QR code, without its quiet zone.</summary>
    /// <param name="text">The text, the address with its token.</param>
    /// <returns>One string per row, <c>1</c> for a dark module and <c>0</c> for a light one; the code is square.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty.</exception>
    internal static IReadOnlyList<string> Rows(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        // QRCoder keeps a quiet zone of four modules around the code: the tab draws its own margin.
        const int Quiet = 4;
        var size = data.ModuleMatrix.Count - 2 * Quiet;
        var rows = new string[size];
        for (var y = 0; y < size; y++)
        {
            var row = data.ModuleMatrix[y + Quiet];
            rows[y] = string.Create(size, row, static (span, modules) =>
            {
                for (var x = 0; x < span.Length; x++) span[x] = modules[x + Quiet] ? '1' : '0';
            });
        }

        return rows;
    }
}
