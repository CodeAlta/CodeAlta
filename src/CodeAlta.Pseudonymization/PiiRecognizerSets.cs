using CodeAlta.Pseudonymization.Presidio;

namespace CodeAlta.Pseudonymization;

/// <summary>
/// The recognizers of the identifiers of the documents of a language: those every country shares and the phone numbers
/// (ported from Presidio), with the identifiers of the countries of the language.
/// </summary>
public static class PiiRecognizerSets
{
    /// <summary>
    /// The regions of the phone numbers of French documents. Luxembourg is not one: its short numbers would take a year
    /// ("2026") for a phone number.
    /// </summary>
    public static IReadOnlyList<string> FrenchRegions { get; } = ["FR", "BE", "CH"];

    /// <summary>The regions of the phone numbers of English documents.</summary>
    public static IReadOnlyList<string> EnglishRegions { get; } = ["GB", "US", "CA", "AU", "IE"];

    /// <summary>Creates the recognizers of French documents: France, and the identifiers every country shares.</summary>
    /// <returns>A new set.</returns>
    public static List<PiiRecognizer> French() => [.. GlobalRecognizers.Create(), new PhoneNumberRecognizer(FrenchRegions), .. FrenchRecognizers.Create()];

    /// <summary>Creates the recognizers of English documents: the United Kingdom and the United States, and the identifiers every country shares.</summary>
    /// <returns>A new set.</returns>
    public static List<PiiRecognizer> English()
        => [.. GlobalRecognizers.Create(), new PhoneNumberRecognizer(EnglishRegions), .. UkRecognizers.Create(), .. UsRecognizers.Create(), .. EnglishRecognizers.Create()];

    /// <summary>Creates the recognizers of documents in several languages: every country above.</summary>
    /// <returns>A new set.</returns>
    public static List<PiiRecognizer> All()
        => [.. GlobalRecognizers.Create(), new PhoneNumberRecognizer([.. FrenchRegions, .. EnglishRegions, .. PhoneNumberRecognizer.DefaultRegions.Except([.. FrenchRegions, .. EnglishRegions])]),
            .. FrenchRecognizers.Create(), .. UkRecognizers.Create(), .. UsRecognizers.Create(), .. EnglishRecognizers.Create()];
}
