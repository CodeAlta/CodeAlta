using System.Globalization;

namespace CodeAlta.ColorSchemes.Generator;

/// <summary>A color in the Oklab space: perceptual lightness (0 black to 1 white) and two chroma axes.</summary>
internal readonly record struct Oklab(double L, double A, double B)
{
    /// <summary>Gets the chroma: the distance from the gray axis.</summary>
    public double Chroma => Math.Sqrt(A * A + B * B);

    /// <summary>Gets the hue angle in radians.</summary>
    public double Hue => Math.Atan2(B, A);

    /// <summary>Creates a color from lightness, chroma and hue (radians).</summary>
    public static Oklab FromLch(double lightness, double chroma, double hue)
        => new(lightness, chroma * Math.Cos(hue), chroma * Math.Sin(hue));

    /// <summary>Parses <c>#rrggbb</c>.</summary>
    public static Oklab FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 7 || hex[0] != '#') throw new FormatException($"'{hex}' is not a #rrggbb color.");
        return FromSrgb(byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>Converts an sRGB color.</summary>
    public static Oklab FromSrgb(byte red, byte green, byte blue)
    {
        double r = ToLinear(red / 255.0), g = ToLinear(green / 255.0), b = ToLinear(blue / 255.0);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>
    /// Formats the color as <c>#rrggbb</c>. A color outside sRGB keeps its lightness and hue and loses chroma
    /// until it fits.
    /// </summary>
    public string ToHex()
    {
        var lightness = Math.Clamp(L, 0, 1);
        var (r, g, b) = Linear(lightness, A, B);
        if (!InGamut(r, g, b))
        {
            double low = 0, high = 1;
            for (var step = 0; step < 24; step++)
            {
                var middle = (low + high) / 2;
                (r, g, b) = Linear(lightness, A * middle, B * middle);
                if (InGamut(r, g, b)) low = middle;
                else high = middle;
            }

            (r, g, b) = Linear(lightness, A * low, B * low);
        }

        return string.Create(CultureInfo.InvariantCulture, $"#{ToByte(r):x2}{ToByte(g):x2}{ToByte(b):x2}");
    }

    private static (double R, double G, double B) Linear(double lightness, double a, double b)
    {
        var l = lightness + 0.3963377774 * a + 0.2158037573 * b;
        var m = lightness - 0.1055613458 * a - 0.0638541728 * b;
        var s = lightness - 0.0894841775 * a - 1.2914855480 * b;
        l *= l * l;
        m *= m * m;
        s *= s * s;
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static bool InGamut(double r, double g, double b)
    {
        const double tolerance = 0.0005;
        return r >= -tolerance && r <= 1 + tolerance && g >= -tolerance && g <= 1 + tolerance && b >= -tolerance && b <= 1 + tolerance;
    }

    private static double ToLinear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static byte ToByte(double linear)
    {
        var value = Math.Clamp(linear, 0, 1);
        value = value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }
}
