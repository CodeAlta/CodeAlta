namespace CodeAlta.Pseudonymization.Tests;

[TestClass]
public sealed class EnglishRecognizerTests
{
    [TestMethod]
    public void TheKeyOfAnEin_IsAPrefixOfTheIrs()
    {
        Assert.IsTrue(PiiChecksums.IsEinValid("12-3456789"));
        Assert.IsFalse(PiiChecksums.IsEinValid("07-3456789"), "07 is no prefix of the IRS.");
    }

    [TestMethod]
    public async Task TheRecognizersOfEnglishDocuments_FindTheIdentifiersOfAFile()
    {
        var text = string.Join("\n",
            "Client: John Smith, born on March 3, 1990.",
            "National Insurance number: AB 12 34 56 C. NHS number 943 476 5919.",
            "SSN 219-09-9999. The company, registered in England under company number 01234567, EIN 12-3456789.",
            "Call 020 7123 4567 or +44 20 7123 4567; in New Jersey (201) 555-0123.",
            "Address: 10 Downing Street, London SW1A 2AA. 350 Fifth Avenue, New York, NY 10118.",
            "Date of birth: 12 May 1985. Signed on 4 June 2024.");

        var spans = await Analyze(text);

        AssertFound(spans, text, PiiEntities.BirthDate, "March 3, 1990");
        AssertFound(spans, text, PiiEntities.BirthDate, "12 May 1985");
        AssertFound(spans, text, PiiEntities.Date, "4 June 2024");
        AssertFound(spans, text, PiiEntities.UkNino, "AB 12 34 56 C");
        AssertFound(spans, text, PiiEntities.UkNhs, "943 476 5919");
        AssertFound(spans, text, PiiEntities.UsSsn, "219-09-9999");
        AssertFound(spans, text, PiiEntities.CompanyNumber, "01234567");
        AssertFound(spans, text, PiiEntities.Ein, "12-3456789");
        AssertFound(spans, text, PiiEntities.Phone, "020 7123 4567");
        AssertFound(spans, text, PiiEntities.Phone, "+44 20 7123 4567");
        AssertFound(spans, text, PiiEntities.Phone, "(201) 555-0123");
        AssertFound(spans, text, PiiEntities.UkPostcode, "SW1A 2AA");
        AssertFound(spans, text, PiiEntities.PostalCode, "10118");
    }

    [TestMethod]
    public async Task NumbersWithoutTheirName_AreNotTakenForIdentifiers()
    {
        // Nine digits, eight digits and two digits with a dash: references, unless their name is beside them. A number
        // with the key of an NHS number is one even alone, as in Presidio.
        const string text = "Invoice 123456789; file 01234567; ref 12-3456789; 45000 dollars.";

        var spans = await Analyze(text);

        Assert.HasCount(0, spans, string.Join(" | ", spans.Select(span => $"{span.Entity}='{text.Substring(span.Start, span.Length)}'")));
    }

    [TestMethod]
    public async Task AYearOrAnAmount_IsNoPhoneNumber_InAnyLanguage()
    {
        const string text = "Signed in 2026 for 45000 EUR, in 1999 and 2024; ref 123456.";
        foreach (var recognizers in new[] { PiiRecognizerSets.French(), PiiRecognizerSets.English(), PiiRecognizerSets.All() })
        {
            var spans = await new PiiAnalyzer(recognizers).AnalyzeAsync(text, CancellationToken.None);
            Assert.IsFalse(spans.Any(static span => span.Entity == PiiEntities.Phone), string.Join(" | ", spans.Select(span => $"{span.Entity}='{text.Substring(span.Start, span.Length)}'")));
        }
    }

    [TestMethod]
    public async Task AFrenchMobileNumber_IsNoNumberOfTheEnglishRegions()
    {
        // A French landline (01...) is also a valid number of the United Kingdom, as libphonenumber reads it: a mobile is not.
        const string text = "Joignable au 06 12 34 56 78.";

        var spans = await Analyze(text);

        Assert.IsFalse(spans.Any(static span => span.Entity == PiiEntities.Phone), "The English documents look for the numbers of their regions.");
    }

    private static async Task<IReadOnlyList<PiiSpan>> Analyze(string text)
        => await new PiiAnalyzer(PiiRecognizerSets.English()).AnalyzeAsync(text, CancellationToken.None);

    private static void AssertFound(IReadOnlyList<PiiSpan> spans, string text, string entity, string value)
        => Assert.IsTrue(spans.Any(span => span.Entity == entity && text.Substring(span.Start, span.Length) == value),
            $"{entity} '{value}' not found in: {string.Join(" | ", spans.Select(span => $"{span.Entity}='{text.Substring(span.Start, span.Length)}'"))}");
}
