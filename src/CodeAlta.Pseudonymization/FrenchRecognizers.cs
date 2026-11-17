using System.Text.RegularExpressions;

namespace CodeAlta.Pseudonymization;

/// <summary>
/// The recognizers of the identifiers of France (NIR, tax number and tax notice, SIREN, SIRET, postal codes, plates, RPPS)
/// and of French dates, with
/// French words of context, which Presidio does not have: they run beside its generic recognizers and its phone numbers
/// (<see cref="PiiRecognizerSets"/>).
/// </summary>
public static partial class FrenchRecognizers
{
    // A French date: "12 mai 1985", "1er juillet 2025", "12/05/1985" or "12.05.1985". A dash is not taken: it would take
    // file numbers ("2014-0883"). A date without a year is not taken.
    private const string DatePattern =
        @"(?:\d{1,2}(?:er)?\s+(?i:janvier|f[ée]vrier|mars|avril|mai|juin|juillet|ao[ûu]t|septembre|octobre|novembre|d[ée]cembre)\s+\d{4}|\d{1,2}[/.]\d{1,2}[/.]\d{4})";

    /// <summary>Creates the recognizers, a new set for each analyzer.</summary>
    /// <returns>The recognizers.</returns>
    public static IReadOnlyList<PiiRecognizer> Create() =>
    [
        new PatternRecognizer(PiiEntities.Nir, NirPattern(), 0.85)
        {
            Validate = static value => PiiChecksums.IsNirValid(value),
            Context = NirContext(),
            SuspectEntity = PiiEntities.NirSuspect,
        },
        // A key modulo 511 lets one number in 511 through: the number is taken without its label, which a notice often
        // prints apart from it ("(C) : 12 34 567 890 066").
        new PatternRecognizer(PiiEntities.TaxNumber, TaxNumberPattern(), 0.75)
        {
            Validate = static value => PiiChecksums.IsFrenchTaxNumberValid(value),
            Context = TaxNumberContext(),
        },
        new PatternRecognizer(PiiEntities.TaxNoticeReference, TaxNoticeReferencePattern(), 0.6)
        {
            Validate = static value => PiiChecksums.IsFrenchTaxNoticeReferenceValid(value),
            Context = TaxNoticeReferenceContext(),
        },
        new PatternRecognizer(PiiEntities.Siret, SiretPattern(), 0.6)
        {
            Validate = static value => PiiChecksums.IsLuhnValid(value),
            Context = SiretContext(),
            SuspectEntity = PiiEntities.SiretSuspect,
        },
        // Nine digits with a good key are common (one in ten): a SIREN counts beside its name only.
        new PatternRecognizer(PiiEntities.Siren, SirenPattern(), 0.3)
        {
            Validate = static value => PiiChecksums.IsLuhnValid(value),
            Context = SirenContext(),
            SuspectEntity = PiiEntities.SirenSuspect,
            SuspectScore = 0.65,
        },
        new PatternRecognizer(PiiEntities.Rpps, RppsPattern(), 0.85) { Context = RppsContext(), ContextRequired = true },
        new PatternRecognizer(PiiEntities.Plate, PlatePattern(), 0.8),
        new PatternRecognizer(PiiEntities.PostalCode, PostalCodePattern(), 0.7) { Group = 1 },
        new PatternRecognizer(PiiEntities.BirthDate, BornOnPattern(), 0.9) { Group = 1 },
        new PatternRecognizer(PiiEntities.BirthDate, BirthDateLabelPattern(), 0.85) { Group = 1 },
        new PatternRecognizer(PiiEntities.Date, DatePatternRegex(), 0.6),
    ];

    [GeneratedRegex(@"(?<![\dA-Za-z])[12][ .]?\d{2}[ .]?\d{2}[ .]?(?:\d{2}|2[ABab])[ .]?\d{3}[ .]?\d{3}[ .]?\d{2}(?![\dA-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex NirPattern();

    [GeneratedRegex(@"(?i)\bNIR\b|s[ée]curit[ée]\s+sociale|\bINSEE\b|\bn°\s*SS\b|\bNSS\b", RegexOptions.CultureInvariant)]
    private static partial Regex NirContext();

    // Thirteen digits, the first 0 to 3, printed "12 34 567 890 066" or in one run.
    [GeneratedRegex(@"(?<![\dA-Za-z])[0-3]\d[ ]?\d{2}[ ]?\d{3}[ ]?\d{3}[ ]?\d{3}(?![\dA-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex TaxNumberPattern();

    [GeneratedRegex(@"(?i)num[ée]ro\s+fiscal|\bn°\s*fiscal|identifiant\s+fiscal|\bSPI\b", RegexOptions.CultureInvariant)]
    private static partial Regex TaxNumberContext();

    // Thirteen digits printed "26 75 1234567 61" (year, department, number, key) or in one run.
    [GeneratedRegex(@"(?<![\dA-Za-z])\d{2}[ ]?\d{2}[ ]?\d{7}[ ]?\d{2}(?![\dA-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex TaxNoticeReferencePattern();

    [GeneratedRegex(@"(?i)r[ée]f[ée]rence\s+de\s+l['’]\s*avis|avis\s+d['’]\s*imp[ôo]t", RegexOptions.CultureInvariant)]
    private static partial Regex TaxNoticeReferenceContext();

    [GeneratedRegex(@"(?<!\d ?)\d{3}[ ]?\d{3}[ ]?\d{3}[ ]?\d{5}(?! ?\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SiretPattern();

    [GeneratedRegex(@"(?i)\bSIRET\b|\bn°\s*d['’]?\s*[ée]tablissement\b", RegexOptions.CultureInvariant)]
    private static partial Regex SiretContext();

    [GeneratedRegex(@"(?<!\d ?)\d{3}[ ]?\d{3}[ ]?\d{3}(?! ?\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SirenPattern();

    [GeneratedRegex(@"(?i)\bSIREN\b|\bRCS\b|\bimmatricul", RegexOptions.CultureInvariant)]
    private static partial Regex SirenContext();

    [GeneratedRegex(@"(?<![\d])\d{11}(?![\d])", RegexOptions.CultureInvariant)]
    private static partial Regex RppsPattern();

    [GeneratedRegex(@"(?i)\bRPPS\b", RegexOptions.CultureInvariant)]
    private static partial Regex RppsContext();

    [GeneratedRegex(@"\b[A-HJ-NP-TV-Z]{2}-\d{3}-[A-HJ-NP-TV-Z]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex PlatePattern();

    // Five digits followed on the same line by the name of a town: a capitalized word ("82000 Montauban"), or a word of
    // capitals as an address block prints it ("33000 BORDEAUX"), not a currency or a unit ("45000 EUR").
    [GeneratedRegex(@"(?<![\d])(\d{5})(?=[ \u00A0]+(?:[A-ZÀÂÄÇÉÈÊËÎÏÔÖÙÛÜŒÆ][a-zà-öø-ÿœæ'’-]|(?!(?:EUR|EUROS|USD|GBP|CHF|TTC|HTVA|KWH|MWH)\b)[A-ZÀÂÄÇÉÈÊËÎÏÔÖÙÛÜŒÆ]{3}))", RegexOptions.CultureInvariant)]
    private static partial Regex PostalCodePattern();

    [GeneratedRegex(@"(?i:\bn[ée]e?(?:\(e\))?\s+le\s*:?\s+)(" + DatePattern + ")", RegexOptions.CultureInvariant)]
    private static partial Regex BornOnPattern();

    [GeneratedRegex(DatePattern, RegexOptions.CultureInvariant)]
    private static partial Regex DatePatternRegex();

    // The label of a field of a form and its date, on the same line or the next: a date further in the sentence is another.
    [GeneratedRegex(@"(?i:\bdate\s+de\s+naissance\b|\bDDN\b)[ \t]*:?[ \t]*\r?\n?[ \t]*(" + DatePattern + ")", RegexOptions.CultureInvariant)]
    private static partial Regex BirthDateLabelPattern();
}
