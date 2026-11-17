namespace CodeAlta.Pseudonymization.Tests;

[TestClass]
public sealed class RecognizerTests
{
    private const string ValidNir = "1 85 05 75 112 345 29";
    private const string ValidCorsicanNir = "2 85 05 2A 112 345 36";
    private const string ValidIban = "FR76 3000 6000 0112 3456 7890 189";

    [TestMethod]
    public void Checksums_TellRealNumbersFromRunsOfDigits()
    {
        Assert.IsTrue(PiiChecksums.IsNirValid(ValidNir));
        Assert.IsTrue(PiiChecksums.IsNirValid(ValidCorsicanNir));
        Assert.IsFalse(PiiChecksums.IsNirValid("1 85 05 75 112 345 28"));
        Assert.IsTrue(PiiChecksums.IsIbanValid(ValidIban));
        Assert.IsFalse(PiiChecksums.IsIbanValid("FR76 3000 6000 0112 3456 7890 188"));
        Assert.IsTrue(PiiChecksums.IsLuhnValid("732 829 320"));
        Assert.IsTrue(PiiChecksums.IsLuhnValid("732 829 320 00074"));
        Assert.IsFalse(PiiChecksums.IsLuhnValid("732 829 321"));
        Assert.IsTrue(PiiChecksums.IsFrenchTaxNumberValid("12 34 567 890 066"));
        Assert.IsFalse(PiiChecksums.IsFrenchTaxNumberValid("12 34 567 890 067"));
        Assert.IsFalse(PiiChecksums.IsFrenchTaxNumberValid("42 34 567 890 066"), "a tax number starts with 0 to 3");
        Assert.IsTrue(PiiChecksums.IsFrenchTaxNoticeReferenceValid("26 75 1234567 61"));
        Assert.IsFalse(PiiChecksums.IsFrenchTaxNoticeReferenceValid("26 75 1234567 60"));
    }

    [TestMethod]
    public async Task ATaxNotice_HidesItsNumbers_AndThePostalCodesOfItsAddressBlocks()
    {
        // The text of a notice as a PDF gives it: the labels apart from their numbers, the address block in capitals.
        var text = string.Join("\n",
            "AVIS_TF_RG",
            " (C) : 12 34 567 890 066",
            "26 75 1234567 61",
            "1 RUE DE LA PAIX BP12",
            "75002 PARIS CEDEX",
            "12 AVENUE DES TILLEULS",
            "33000 BORDEAUX",
            "13001 MARSEILLE CEDEX 01",
            "Base 4101 4101 4101 4101 4101",
            "Somme à payer 45000 EUR, identifiant service 69002.");

        var spans = await Analyze(text);

        AssertFound(spans, text, PiiEntities.TaxNumber, "12 34 567 890 066");
        AssertFound(spans, text, PiiEntities.TaxNoticeReference, "26 75 1234567 61");
        AssertFound(spans, text, PiiEntities.PostalCode, "75002");
        AssertFound(spans, text, PiiEntities.PostalCode, "33000");
        AssertFound(spans, text, PiiEntities.PostalCode, "13001");
        string[] notPersonal = ["45000", "69002"];
        Assert.IsFalse(spans.Any(span => span.Entity == PiiEntities.PostalCode && notPersonal.Contains(text.Substring(span.Start, span.Length))),
            "an amount and a number alone are no postal codes");
        Assert.IsFalse(spans.Any(static span => span.Entity == PiiEntities.CreditCard), "the same base in each column is no card");
    }

    [TestMethod]
    public async Task ANumberWithTheWrongKey_IsNoTaxNumber()
    {
        const string text = "Numéro fiscal : 12 34 567 890 067, référence de l'avis 26 75 1234567 60.";
        var spans = await Analyze(text);
        Assert.IsFalse(spans.Any(static span => span.Entity is PiiEntities.TaxNumber or PiiEntities.TaxNoticeReference));
    }

    [TestMethod]
    public async Task TheFrenchRecognizers_FindTheIdentifiersOfAnAct()
    {
        var text = string.Join("\n",
            "Entre les soussignés :",
            "Madame Sophie Martin, née le 12 mai 1985 à Lyon,",
            $"numéro de sécurité sociale {ValidNir},",
            "demeurant 14 rue des Lilas, 82000 Montauban,",
            "joignable au 06 12 34 56 78 ou à sophie.martin@exemple.fr,",
            $"IBAN : {ValidIban}.",
            "La société, immatriculée au RCS sous le SIREN 732 829 320, SIRET 732 829 320 00074.",
            "Fait à Montauban, le 3 mars 2026.");

        var spans = await Analyze(text);

        AssertFound(spans, text, PiiEntities.BirthDate, "12 mai 1985");
        AssertFound(spans, text, PiiEntities.Nir, ValidNir);
        AssertFound(spans, text, PiiEntities.PostalCode, "82000");
        AssertFound(spans, text, PiiEntities.Phone, "06 12 34 56 78");
        AssertFound(spans, text, PiiEntities.Email, "sophie.martin@exemple.fr");
        AssertFound(spans, text, PiiEntities.Iban, ValidIban);
        AssertFound(spans, text, PiiEntities.Siren, "732 829 320");
        AssertFound(spans, text, PiiEntities.Siret, "732 829 320 00074");
        AssertFound(spans, text, PiiEntities.Date, "3 mars 2026");
        Assert.IsFalse(spans.Any(span => span.Entity == PiiEntities.BirthDate && text.Substring(span.Start, span.Length) == "3 mars 2026"), "The date of the act is not a date of birth.");
    }

    [TestMethod]
    public async Task OnlyTheDateThatFollowsItsLabel_IsADateOfBirth()
    {
        const string text = "Mme Sophie Martin, née le 12 mai 1985, a signé l'acte le 3 mars 2026. Date de naissance : 28/09/1986, entrée le 01/02/2020.";

        var spans = await Analyze(text);

        AssertFound(spans, text, PiiEntities.BirthDate, "12 mai 1985");
        AssertFound(spans, text, PiiEntities.BirthDate, "28/09/1986");
        AssertFound(spans, text, PiiEntities.Date, "3 mars 2026");
        AssertFound(spans, text, PiiEntities.Date, "01/02/2020");
    }

    [TestMethod]
    public async Task ANumberWithAWrongKey_IsSuspectBesideItsName_AndIgnoredElsewhere()
    {
        var named = "N° de sécurité sociale : 1 85 05 75 112 345 28";
        AssertFound(await Analyze(named), named, PiiEntities.NirSuspect, "1 85 05 75 112 345 28");

        var plain = "Référence interne 1 85 05 75 112 345 28.";
        Assert.IsFalse((await Analyze(plain)).Any(static span => span.Entity is PiiEntities.Nir or PiiEntities.NirSuspect));
    }

    [TestMethod]
    public async Task NineDigitsWithAGoodKey_AreASirenOnlyBesideItsName()
    {
        var plain = "Commande 732 829 320 livrée.";
        Assert.IsFalse((await Analyze(plain)).Any(static span => span.Entity == PiiEntities.Siren));
        var named = "SIREN : 732829320";
        AssertFound(await Analyze(named), named, PiiEntities.Siren, "732829320");
    }

    [TestMethod]
    public void Overlaps_KeepTheSurestKind_AndWhatSticksOut()
    {
        const string text = "Contact : Marie Martin (marie@exemple.fr)";
        var email = text.IndexOf("marie@", StringComparison.Ordinal);
        var name = text.IndexOf("Marie", StringComparison.Ordinal);
        var resolved = PiiAnalyzer.Resolve(text,
        [
            new PiiSpan(PiiEntities.Person, name, text.Length - name, 0.8, "ner"),
            new PiiSpan(PiiEntities.Email, email, "marie@exemple.fr".Length, 0.95, "pattern"),
        ]);

        Assert.HasCount(2, resolved);
        Assert.AreEqual("Marie Martin", text.Substring(resolved[0].Start, resolved[0].Length));
        Assert.AreEqual(PiiEntities.Person, resolved[0].Entity);
        Assert.AreEqual(PiiEntities.Email, resolved[1].Entity);
    }

    [TestMethod]
    public void TwoPiecesOfOneName_AreOne()
    {
        const string text = "avenue de l'Europe";
        var resolved = PiiAnalyzer.Resolve(text,
        [
            new PiiSpan(PiiEntities.Location, 0, 12, 0.7, "ner"),
            new PiiSpan(PiiEntities.Location, 12, 6, 0.9, "ner"),
        ]);

        Assert.HasCount(1, resolved);
        Assert.AreEqual(text.Length, resolved[0].Length);
    }

    private static async Task<IReadOnlyList<PiiSpan>> Analyze(string text)
        => await new PiiAnalyzer(PiiRecognizerSets.French()).AnalyzeAsync(text, CancellationToken.None);

    private static void AssertFound(IReadOnlyList<PiiSpan> spans, string text, string entity, string value)
        => Assert.IsTrue(spans.Any(span => span.Entity == entity && text.Substring(span.Start, span.Length) == value),
            $"{entity} '{value}' not found in: {string.Join(" | ", spans.Select(span => $"{span.Entity}='{text.Substring(span.Start, span.Length)}'"))}");
}
