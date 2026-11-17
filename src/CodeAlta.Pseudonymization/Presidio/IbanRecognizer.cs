using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace CodeAlta.Pseudonymization.Presidio;

/// <summary>
/// The IBAN recognizer of Presidio (MIT, Microsoft; <c>generic/iban_recognizer.py</c>, <c>iban_patterns.py</c>): a generic
/// pattern whose match is tried whole, then without its last groups, until one has a good key and the format of its
/// country (score 1); a good key in a format that only fits in capitals keeps the score of the pattern. As Presidio, it
/// is case-sensitive, and the format of the country is checked from the start of the value.
/// </summary>
public sealed class IbanRecognizer : PresidioPatternRecognizer
{
    private static readonly Regex Generic = new(
        @"(?<![A-Z0-9])([A-Z]{2}[0-9]{2}(?:[ -]?[A-Z0-9]{4}){2,6})((?:[ -]?[A-Z0-9]{4})?)((?:[ -]?[A-Z0-9]{1,3})?)(?![A-Z0-9])",
        CaseSensitive, TimeSpan.FromSeconds(5));

    // IbanRecognizer compiles its pattern with DOTALL | MULTILINE, without IGNORECASE.
    private const RegexOptions CaseSensitive = RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    /// <summary>Creates the recognizer, which finds <see cref="PiiEntities.Iban"/>.</summary>
    public IbanRecognizer()
        : base(PiiEntities.Iban, [new("IBAN Generic", Generic.ToString(), 0.5)], ["iban", "bank", "transaction"], CaseSensitive)
    {
    }

    /// <inheritdoc />
    public override IReadOnlyList<PiiSpan> AnalyzeRaw(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<PiiSpan>? spans = null;
        foreach (Match match in Generic.Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var group = match.Groups.Count - 1; group >= 1; group--)
            {
                var end = match.Groups[group].Index + match.Groups[group].Length;
                if (end <= 0) end = match.Index + match.Length;
                var value = text[match.Index..end];
                if (value.Length == 0) continue;
                var score = Check(value) switch { true => 1d, false => 0d, null => 0.5 };
                if (score <= 0) continue;
                (spans ??= []).Add(new PiiSpan(Entity, match.Index, value.Length, score, Name));
                break;
            }
        }

        return spans ?? (IReadOnlyList<PiiSpan>)[];
    }

    // IbanRecognizer.validate_result: the key, then the format of the country; null for a format that fits in capitals only.
    private static bool? Check(string value)
    {
        var compact = UsRecognizers.Sanitize(value, '-', ' ');
        if (compact.Length < 5 || !PiiChecksums.IsIbanValid(compact)) return false;
        if (Formats.TryGetValue(compact[..2].ToUpperInvariant(), out var format))
        {
            if (format.IsMatch(compact)) return true;
            if (format.IsMatch(compact.ToUpperInvariant())) return null;
        }

        return false;
    }

    // The format of the IBAN of each country (iban_patterns.py), from the start of the value (re.match without exact_match),
    // case-sensitive as Presidio checks it.
    private static readonly FrozenDictionary<string, Regex> Formats = BuildFormats();

    private static FrozenDictionary<string, Regex> BuildFormats()
    {
        const string ck = "[0-9]{2}[ ]?", a = "[A-Z][ ]?", a3 = "([A-Z][ ]?){3}", a4 = "([A-Z][ ]?){4}";
        const string c = "[a-zA-Z0-9][ ]?", c2 = "([a-zA-Z0-9][ ]?){2}", c3 = "([a-zA-Z0-9][ ]?){3}", c4 = "([a-zA-Z0-9][ ]?){4}";
        const string n = "[0-9][ ]?", n2 = "([0-9][ ]?){2}", n3 = "([0-9][ ]?){3}", n4 = "([0-9][ ]?){4}";
        var formats = new Dictionary<string, string>
        {
            ["AL"] = ck + n4 + n4 + c4 + c4 + c4 + c4, ["AD"] = ck + n4 + n4 + c4 + c4 + c4, ["AT"] = ck + n4 + n4 + n4 + n4,
            ["AZ"] = ck + c4 + n4 + n4 + n4 + n4 + n4, ["BH"] = ck + a4 + c4 + c4 + c4 + c2, ["BY"] = ck + c4 + n4 + c4 + c4 + c4 + c4,
            ["BE"] = ck + n4 + n4 + n4, ["BA"] = ck + n4 + n4 + n4 + n4, ["BR"] = ck + n4 + n4 + n4 + n4 + n4 + n3 + a + c,
            ["BG"] = ck + a4 + n4 + n + n + c2 + c4 + c2, ["CR"] = ck + "[0]" + n3 + n4 + n4 + n4 + n2, ["HR"] = ck + n4 + n4 + n4 + n4 + n,
            ["CY"] = ck + n4 + n4 + c4 + c4 + c4 + c4, ["CZ"] = ck + n4 + n4 + n4 + n4 + n4, ["DK"] = ck + n4 + n4 + n4 + n2,
            ["DO"] = ck + a4 + n4 + n4 + n4 + n4 + n4, ["TL"] = ck + n4 + n4 + n4 + n4 + n3, ["EE"] = ck + n4 + n4 + n4 + n4,
            ["FO"] = ck + n4 + n4 + n4 + n2, ["FI"] = ck + n4 + n4 + n4 + n2, ["FR"] = ck + n4 + n4 + n2 + c2 + c4 + c4 + c + n2,
            ["GE"] = ck + c2 + n2 + n4 + n4 + n4 + n2, ["DE"] = ck + n4 + n4 + n4 + n4 + n2, ["GI"] = ck + a4 + c4 + c4 + c4 + c3,
            ["GR"] = ck + n4 + n3 + c + c4 + c4 + c4 + c3, ["GL"] = ck + n4 + n4 + n4 + n2, ["GT"] = ck + c4 + c4 + c4 + c4 + c4 + c4,
            ["HU"] = ck + n4 + n4 + n4 + n4 + n4 + n4, ["IS"] = ck + n4 + n4 + n4 + n4 + n4 + n2, ["IE"] = ck + c4 + n4 + n4 + n4 + n2,
            ["IL"] = ck + n4 + n4 + n4 + n4 + n3, ["IT"] = ck + a + n3 + n4 + n3 + c + c3 + c + c4 + c3, ["JO"] = ck + a4 + n4 + n4 + n4 + n4 + n4 + n2,
            ["KZ"] = ck + n3 + c + c4 + c4 + c4, ["XK"] = ck + n4 + n4 + n4 + n4, ["KW"] = ck + a4 + c4 + c4 + c4 + c4 + c4 + c2,
            ["LV"] = ck + a4 + c4 + c4 + c4 + c, ["LB"] = ck + n4 + c4 + c4 + c4 + c4 + c4, ["LI"] = ck + n4 + n + c3 + c4 + c4 + c,
            ["LT"] = ck + n4 + n4 + n4 + n4, ["LU"] = ck + n3 + c + c4 + c4 + c4, ["MT"] = ck + a4 + n4 + n + c3 + c4 + c4 + c4 + c3,
            ["MR"] = ck + n4 + n4 + n4 + n4 + n4 + n3, ["MU"] = ck + a4 + n4 + n4 + n4 + n4 + n3 + a, ["MD"] = ck + c4 + c4 + c4 + c4 + c4,
            ["MC"] = ck + n4 + n4 + n2 + c2 + c4 + c4 + c + n2, ["ME"] = ck + n4 + n4 + n4 + n4 + n2, ["NL"] = ck + a4 + n4 + n4 + n2,
            ["MK"] = ck + n3 + c + c4 + c4 + c + n2, ["NO"] = ck + n4 + n4 + n3, ["PK"] = ck + c4 + n4 + n4 + n4 + n4,
            ["PS"] = ck + c4 + n4 + n4 + n4 + n4 + n, ["PL"] = ck + n4 + n4 + n4 + n4 + n4 + n4, ["PT"] = ck + n4 + n4 + n4 + n4 + n,
            ["QA"] = ck + a4 + c4 + c4 + c4 + c4 + c, ["RO"] = ck + a4 + c4 + c4 + c4 + c4, ["SM"] = ck + a + n3 + n4 + n3 + c + c4 + c4 + c3,
            ["SA"] = ck + n2 + c2 + c4 + c4 + c4 + c4, ["RS"] = ck + n4 + n4 + n4 + n4 + n2, ["SK"] = ck + n4 + n4 + n4 + n4 + n4,
            ["SI"] = ck + n4 + n4 + n4 + n3, ["ES"] = ck + n4 + n4 + n4 + n4 + n4, ["SE"] = ck + n4 + n4 + n4 + n4 + n4,
            ["CH"] = ck + n4 + n + c3 + c4 + c4 + c, ["TN"] = ck + n4 + n4 + n4 + n4 + n4, ["TR"] = ck + n4 + n + c3 + c4 + c4 + c4 + c2,
            ["AE"] = ck + n4 + n4 + n4 + n4 + n3, ["GB"] = ck + a4 + n4 + n4 + n4 + n2, ["VA"] = ck + n4 + n4 + n4 + n4 + n2,
            ["VG"] = ck + c4 + n4 + n4 + n4 + n4, ["EG"] = ck + n4 + n4 + n4 + n4 + n4 + n4 + n, ["IQ"] = ck + a4 + n4 + n4 + n4 + n3,
            ["LY"] = ck + n4 + n4 + n4 + n4 + n4 + n, ["LC"] = ck + a4 + c4 + c4 + c4 + c4 + c4 + c4, ["SC"] = ck + a4 + n4 + n4 + n4 + n4 + n4 + a3,
            ["UA"] = ck + n4 + n2 + c4 + c4 + c4 + c4 + c3,
        };
        return formats.ToFrozenDictionary(static pair => pair.Key,
            static pair => new Regex(@"\A(" + pair.Key + ")" + pair.Value, RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant),
            StringComparer.Ordinal);
    }
}
