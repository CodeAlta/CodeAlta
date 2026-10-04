using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeAlta.ColorSchemes.Generator;
using XenoAtom.Terminal.UI.Styling;

// Generates the color schemes of the desktop WebApp from the RootLoops color schemes of XenoAtom.Terminal.UI.
//
//   dotnet run --project src/CodeAlta.ColorSchemes.Generator [-- <frontend directory>]
//
// Run it after the scheme recipes of XenoAtom.Terminal.UI or the Blueprint palette changed. It reads the
// Blueprint palette from the frontend's node_modules and writes src/colorSchemes.gen.css and
// src/colorSchemes.gen.ts there. A scheme only redefines Blueprint's palette variables and keeps the lightness
// of every palette step: Blueprint derives further colors from its palette by lightness arithmetic, and the
// contrast it designed its components for is preserved.

var frontend = args.Length > 0 ? Path.GetFullPath(args[0]) : FindFrontend();
var blueprintCss = Path.Combine(frontend, "node_modules", "@blueprintjs", "core", "lib", "css", "blueprint.css");
if (!File.Exists(blueprintCss))
{
    Console.Error.WriteLine($"Blueprint's stylesheet was not found at {blueprintCss}. Build the frontend once to restore its packages.");
    return 1;
}

var palette = BlueprintPalette.Read(File.ReadAllText(blueprintCss));
var predefined = ColorScheme.GetPredefinedSchemes();
var schemes = new List<GeneratedScheme>();
foreach (var dark in predefined.Where(static scheme => scheme.Name.EndsWith(" Dark Soft", StringComparison.Ordinal)))
{
    var fruit = dark.Name[..^" Dark Soft".Length];
    var light = predefined.FirstOrDefault(scheme => scheme.Name == fruit + " Light Soft");
    if (light is null) continue;
    schemes.Add(new(fruit.ToLowerInvariant().Replace(' ', '-'), fruit, SchemePalette.Derive(palette, dark, light: false), SchemePalette.Derive(palette, light, light: true)));
}

if (schemes.Count == 0)
{
    Console.Error.WriteLine("XenoAtom.Terminal.UI defines no 'Dark Soft' / 'Light Soft' scheme pair.");
    return 1;
}

var source = Path.Combine(frontend, "src");
File.WriteAllText(Path.Combine(source, "colorSchemes.gen.css"), Emit.Css(schemes).ReplaceLineEndings(), new UTF8Encoding(false));
File.WriteAllText(Path.Combine(source, "colorSchemes.gen.ts"), Emit.TypeScript(schemes).ReplaceLineEndings(), new UTF8Encoding(false));
Console.WriteLine($"Wrote {schemes.Count} color schemes to {source}.");
return 0;

static string FindFrontend()
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            foreach (var candidate in new[] { directory.FullName, Path.Combine(directory.FullName, "src") })
            {
                var frontend = Path.Combine(candidate, "CodeAlta", "frontend");
                if (File.Exists(Path.Combine(candidate, "CodeAlta.slnx")) && Directory.Exists(frontend)) return frontend;
            }
        }
    }

    throw new InvalidOperationException("The frontend directory was not found; pass it as the first argument.");
}

namespace CodeAlta.ColorSchemes.Generator
{
    /// <summary>One generated scheme: the palette overrides of its dark and light variants.</summary>
    internal sealed record GeneratedScheme(string Id, string Name, SchemePalette Dark, SchemePalette Light);

    /// <summary>Blueprint's palette: every <c>--bp-palette-*</c> color of its stylesheet, in declaration order.</summary>
    internal sealed partial class BlueprintPalette
    {
        private static readonly string[] NeutralFamilies = ["black", "dark-gray", "gray", "light-gray", "white"];

        private BlueprintPalette(IReadOnlyList<(string Name, Oklab Color)> colors) => Colors = colors;

        /// <summary>Gets the colors by variable name without the <c>--bp-palette-</c> prefix (<c>dark-gray-1</c>).</summary>
        public IReadOnlyList<(string Name, Oklab Color)> Colors { get; }

        public Oklab this[string name] => Colors.First(color => color.Name == name).Color;

        public static BlueprintPalette Read(string css)
        {
            var colors = new List<(string, Oklab)>();
            foreach (Match match in Declaration().Matches(css))
            {
                var name = match.Groups[1].Value;
                if (colors.All(color => color.Item1 != name)) colors.Add((name, Oklab.FromHex(match.Groups[2].Value)));
            }

            if (colors.Count == 0) throw new InvalidOperationException("Blueprint's stylesheet declares no --bp-palette-* color.");
            return new(colors);
        }

        /// <summary>Splits <c>dark-gray-3</c> into its family (<c>dark-gray</c>) and step (3; 0 for black and white).</summary>
        public static (string Family, int Step) Split(string name)
        {
            var dash = name.LastIndexOf('-');
            return dash > 0 && int.TryParse(name.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var step) ? (name[..dash], step) : (name, 0);
        }

        public static bool IsNeutral(string name) => Array.IndexOf(NeutralFamilies, Split(name).Family) >= 0;

        [GeneratedRegex(@"--bp-palette-([a-z0-9-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;")]
        private static partial Regex Declaration();
    }

    /// <summary>The palette of one scheme variant: Blueprint's variables with the colors of the scheme.</summary>
    internal sealed record SchemePalette(IReadOnlyList<(string Name, string Hex)> Neutrals, IReadOnlyList<(string Name, string Hex)> Accents)
    {
        public string this[string name] => Neutrals.Concat(Accents).First(color => color.Name == name).Hex;

        public static SchemePalette Derive(BlueprintPalette blueprint, ColorScheme scheme, bool light)
        {
            // The neutral ramp of the scheme, from its darkest to its lightest color.
            var ramp = new[] { scheme.Background!.Value, scheme.Black, scheme.BrightBlack, scheme.White, scheme.BrightWhite, scheme.Foreground!.Value }
                .Select(Convert).OrderBy(static color => color.L).ToArray();
            var background = Convert(scheme.Background.Value);
            var foreground = Convert(scheme.Foreground.Value);
            // Blueprint's window background and text for the variant take the tint of the scheme's background and
            // foreground, and every other gray the tint at its relative place between them.
            var from = blueprint[light ? "light-gray-5" : "dark-gray-1"].L;
            var to = blueprint[light ? "dark-gray-1" : "light-gray-5"].L;
            var neutrals = new List<(string, string)>();
            var accents = new List<(string, string)>();
            var schemeAccents = new[] { scheme.Red, scheme.Green, scheme.Yellow, scheme.Blue, scheme.Purple, scheme.Cyan }.Select(Convert).ToArray();
            foreach (var family in blueprint.Colors.Where(static color => !BlueprintPalette.IsNeutral(color.Name)).GroupBy(static color => BlueprintPalette.Split(color.Name).Family))
            {
                // A Blueprint family takes the hue of the scheme accent nearest to it and that accent's share of chroma.
                var steps = family.ToArray();
                var middle = steps[steps.Length / 2].Color;
                var accent = schemeAccents.MinBy(candidate => HueDistance(candidate.Hue, middle.Hue));
                var reference = steps.MinBy(step => Math.Abs(step.Color.L - accent.L)).Color;
                var ratio = Math.Clamp(accent.Chroma / Math.Max(reference.Chroma, 0.001), 0.5, 1.2);
                foreach (var (name, color) in steps) accents.Add((name, Oklab.FromLch(color.L, color.Chroma * ratio, accent.Hue).ToHex()));
            }

            foreach (var (name, color) in blueprint.Colors.Where(static color => BlueprintPalette.IsNeutral(color.Name)))
            {
                var position = (color.L - from) / (to - from);
                var tint = At(ramp, background.L + position * (foreground.L - background.L));
                neutrals.Add((name, new Oklab(color.L, tint.A, tint.B).ToHex()));
            }

            return new(neutrals, accents);
        }

        // The color of the ramp at a lightness; beyond its ends the chroma fades toward black or white.
        private static Oklab At(Oklab[] ramp, double lightness)
        {
            lightness = Math.Clamp(lightness, 0, 1);
            var first = ramp[0];
            var last = ramp[^1];
            if (lightness <= first.L)
            {
                var scale = first.L <= 0 ? 0 : lightness / first.L;
                return new(lightness, first.A * scale, first.B * scale);
            }

            if (lightness >= last.L)
            {
                var scale = last.L >= 1 ? 0 : (1 - lightness) / (1 - last.L);
                return new(lightness, last.A * scale, last.B * scale);
            }

            for (var index = 1; index < ramp.Length; index++)
            {
                var (low, high) = (ramp[index - 1], ramp[index]);
                if (lightness > high.L) continue;
                var amount = high.L - low.L <= 0 ? 0 : (lightness - low.L) / (high.L - low.L);
                return new(lightness, low.A + (high.A - low.A) * amount, low.B + (high.B - low.B) * amount);
            }

            return last;
        }

        private static double HueDistance(double left, double right)
        {
            var distance = Math.Abs(left - right) % (2 * Math.PI);
            return distance > Math.PI ? 2 * Math.PI - distance : distance;
        }

        private static Oklab Convert(XenoAtom.Terminal.UI.Color color)
        {
            var rgb = color.ToRgb();
            return Oklab.FromSrgb(rgb.R, rgb.G, rgb.B);
        }
    }

    /// <summary>Writes the generated files.</summary>
    internal static class Emit
    {
        private const string Notice = "Generated by src/CodeAlta.ColorSchemes.Generator from the RootLoops color schemes of XenoAtom.Terminal.UI. Do not edit: run the tool again.";

        public static string Css(IReadOnlyList<GeneratedScheme> schemes)
        {
            var builder = new StringBuilder();
            builder.Append("/* ").Append(Notice).Append(" */\n");
            builder.Append("/* A scheme redefines Blueprint's palette for the dark theme and, with data-theme=\"light\", for the light one. */\n");
            // The accents of the RootLoops schemes do not depend on the fruit: one rule serves every scheme then.
            var accents = schemes.SelectMany(static scheme => new[] { scheme.Dark, scheme.Light }).Select(static palette => Declarations(palette.Accents)).Distinct().ToArray();
            var shared = accents.Length == 1;
            if (shared) builder.Append(":root[data-color-scheme] {\n").Append(accents[0]).Append("}\n");
            foreach (var scheme in schemes)
            {
                builder.Append(CultureInfo.InvariantCulture, $":root[data-color-scheme=\"{scheme.Id}\"] {{\n").Append(Declarations(scheme.Dark.Neutrals));
                if (!shared) builder.Append(Declarations(scheme.Dark.Accents));
                builder.Append("}\n");
                builder.Append(CultureInfo.InvariantCulture, $":root[data-color-scheme=\"{scheme.Id}\"][data-theme=\"light\"] {{\n").Append(Declarations(scheme.Light.Neutrals));
                if (!shared) builder.Append(Declarations(scheme.Light.Accents));
                builder.Append("}\n");
            }

            return builder.ToString();
        }

        public static string TypeScript(IReadOnlyList<GeneratedScheme> schemes)
        {
            var builder = new StringBuilder();
            builder.Append("// ").Append(Notice).Append('\n');
            builder.Append("/** The colors that stand for a scheme variant in a picker. */\n");
            builder.Append("export type ColorSchemeSwatch = Readonly<{ background: string; surface: string; tint: string; foreground: string; accent: string }>;\n");
            builder.Append("/** The generated schemes: the id is the value of the root's data-color-scheme attribute. */\n");
            builder.Append("export const generatedColorSchemes: readonly Readonly<{ id: string; name: string; dark: ColorSchemeSwatch; light: ColorSchemeSwatch }>[] = [\n");
            foreach (var scheme in schemes)
            {
                builder.Append(CultureInfo.InvariantCulture, $"  {{ id: \"{scheme.Id}\", name: \"{scheme.Name}\",\n");
                builder.Append(CultureInfo.InvariantCulture, $"    dark: {Swatch(scheme.Dark, "dark-gray-1", "dark-gray-3", "gray-1", "light-gray-5", "blue-4")},\n");
                builder.Append(CultureInfo.InvariantCulture, $"    light: {Swatch(scheme.Light, "light-gray-5", "white", "gray-4", "dark-gray-1", "blue-3")} }},\n");
            }

            builder.Append("];\n");
            return builder.ToString();
        }

        private static string Swatch(SchemePalette palette, string background, string surface, string tint, string foreground, string accent)
            => $"{{ background: \"{palette[background]}\", surface: \"{palette[surface]}\", tint: \"{palette[tint]}\", foreground: \"{palette[foreground]}\", accent: \"{palette[accent]}\" }}";

        private static string Declarations(IEnumerable<(string Name, string Hex)> colors)
        {
            var builder = new StringBuilder();
            foreach (var (name, hex) in colors) builder.Append(CultureInfo.InvariantCulture, $"  --bp-palette-{name}: {hex};\n");
            return builder.ToString();
        }
    }
}
