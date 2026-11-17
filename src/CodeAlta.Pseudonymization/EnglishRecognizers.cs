using System.Text.RegularExpressions;

namespace CodeAlta.Pseudonymization;

/// <summary>
/// The recognizers of English documents that Presidio does not have: US employer identification numbers, UK company
/// numbers, US ZIP codes, English dates and dates of birth. They run beside the recognizers of the United Kingdom and of
/// the United States ported from Presidio (<see cref="Presidio.UkRecognizers"/>, <see cref="Presidio.UsRecognizers"/>).
/// </summary>
public static partial class EnglishRecognizers
{
    // An English date: "12 May 1985", "May 12, 1985", "12th of May 1985" or "05/12/1985". A date without a year is not taken.
    private const string Month = "(?i:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";
    private const string DatePattern =
        @"(?:\d{1,2}(?:st|nd|rd|th)?(?:\s+of)?\s+" + Month + @"\.?,?\s+\d{4}|" + Month + @"\.?\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4}|\d{1,2}/\d{1,2}/\d{4})";

    /// <summary>Creates the recognizers, a new set for each analyzer.</summary>
    /// <returns>The recognizers.</returns>
    public static IReadOnlyList<PiiRecognizer> Create() =>
    [
        new PatternRecognizer(PiiEntities.Ein, EinPattern(), 0.85) { Validate = static value => PiiChecksums.IsEinValid(value), Context = EinContext(), ContextRequired = true },
        new PatternRecognizer(PiiEntities.CompanyNumber, CompanyNumberPattern(), 0.85) { Context = CompanyNumberContext(), ContextRequired = true },
        new PatternRecognizer(PiiEntities.PostalCode, ZipPattern(), 0.7) { Group = 1 },
        new PatternRecognizer(PiiEntities.BirthDate, BornOnPattern(), 0.9) { Group = 1 },
        new PatternRecognizer(PiiEntities.BirthDate, BirthDateLabelPattern(), 0.85) { Group = 1 },
        new PatternRecognizer(PiiEntities.Date, DatePatternRegex(), 0.6),
    ];

    [GeneratedRegex(@"(?<![\d-])\d{2}-\d{7}(?![\d-])", RegexOptions.CultureInvariant)]
    private static partial Regex EinPattern();

    [GeneratedRegex(@"(?i)\bF?EIN\b|\bemployer\s+identification\b|\btax\s+(?:id|identification)\b|\bTIN\b", RegexOptions.CultureInvariant)]
    private static partial Regex EinContext();

    [GeneratedRegex(@"\b(?:\d{8}|(?:SC|NI|OC|SO|NC|LP|SL|NL|R0)\d{6})\b", RegexOptions.CultureInvariant)]
    private static partial Regex CompanyNumberPattern();

    [GeneratedRegex(@"(?i)\bcompan(?:y|ies)\s+(?:no\b|number|registration)|\bcompanies\s+house\b|\bregistered\s+(?:in\s+(?:England|Scotland|Wales)|number|no\b)", RegexOptions.CultureInvariant)]
    private static partial Regex CompanyNumberContext();

    // Five digits after the abbreviation of a state: a ZIP code of an address, not an amount.
    [GeneratedRegex(@"\b(?:A[LKZR]|C[AOT]|D[EC]|FL|GA|HI|I[ADLN]|K[SY]|LA|M[EDAINSOT]|N[EVHJMYCD]|O[HKR]|PA|RI|S[CD]|T[NX]|UT|V[TA]|W[AVIY])[ ,]+(\d{5}(?:-\d{4})?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ZipPattern();

    [GeneratedRegex(@"(?i:\bborn\s+(?:on\s+)?)(" + DatePattern + ")", RegexOptions.CultureInvariant)]
    private static partial Regex BornOnPattern();

    // The label of a field of a form and its date, on the same line or the next: a date further in the sentence is another.
    [GeneratedRegex(@"(?i:\bdate\s+of\s+birth\b|\bD\.?O\.?B\b\.?|\bbirth\s*date\b)[ \t]*:?[ \t]*\r?\n?[ \t]*(" + DatePattern + ")", RegexOptions.CultureInvariant)]
    private static partial Regex BirthDateLabelPattern();

    [GeneratedRegex(DatePattern, RegexOptions.CultureInvariant)]
    private static partial Regex DatePatternRegex();
}
