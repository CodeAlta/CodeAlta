namespace CodeAlta.Fleet.Identity;

/// <summary>The base32 alphabet of RFC 4648, without padding: what people read and type of a key.</summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Encodes bytes in upper-case base32 without padding.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var length = (data.Length * 8 + 4) / 5;
        Span<char> chars = length <= 256 ? stackalloc char[length] : new char[length];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                chars[index++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            chars[index++] = Alphabet[(buffer << (5 - bits)) & 31];
        }

        return new string(chars[..index]);
    }
}
