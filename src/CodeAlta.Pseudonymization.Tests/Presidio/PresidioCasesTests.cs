using System.Text.Json;
using CodeAlta.Pseudonymization.Presidio;

namespace CodeAlta.Pseudonymization.Tests.Presidio;

/// <summary>
/// The cases of the tests of the predefined recognizers of Presidio (presidio-cases.json, MIT, Microsoft), which the ported
/// recognizers pass as Presidio's do: the same spans, with scores in the same ranges, before any context.
/// </summary>
[TestClass]
public sealed class PresidioCasesTests
{
    private const double Error = 0.00001;

    public static IEnumerable<object[]> Cases()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Presidio", "presidio-cases.json"));
        using var document = JsonDocument.Parse(file);
        var index = 0;
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [$"{item.GetProperty("entity").GetString()} #{index++}", item.GetRawText()];
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void TheRecognizerPorted_FindsWhatPresidiosFinds(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        var text = item.GetProperty("text").GetString()!;
        var recognizer = RecognizerOf(item.GetProperty("entity").GetString()!, item.TryGetProperty("regions", out var regions) ? [.. regions.EnumerateArray().Select(static region => region.GetString()!)] : null);

        var results = recognizer.AnalyzeRaw(text, CancellationToken.None).OrderBy(static span => span.Start).ThenBy(static span => span.End).ToList();

        var found = string.Join(" | ", results.Select(span => $"[{span.Start}, {span.End}) '{text[span.Start..span.End]}' {span.Score}"));
        Assert.AreEqual(item.GetProperty("count").GetInt32(), results.Count, $"{name} in '{text}': {found}");
        var spans = item.GetProperty("spans").EnumerateArray().ToList();
        var minimums = item.GetProperty("min").EnumerateArray().ToList();
        var maximums = item.GetProperty("max").EnumerateArray().ToList();
        for (var position = 0; position < Math.Min(results.Count, spans.Count); position++)
        {
            var result = results[position];
            Assert.AreEqual((spans[position][0].GetInt32(), spans[position][1].GetInt32()), (result.Start, result.End), $"{name} in '{text}': {found}");
            if (position >= minimums.Count) continue;
            Assert.IsTrue(result.Score >= Math.Max(0, minimums[position].GetDouble() - Error) && result.Score <= Math.Min(1, maximums[position].GetDouble() + Error),
                $"{name} in '{text}': the score {result.Score} is not in [{minimums[position]}, {maximums[position]}]: {found}");
        }
    }

    // The recognizer ported for a kind of Presidio.
    private static PresidioPatternRecognizer RecognizerOf(string entity, string[]? regions)
    {
        var kind = entity switch
        {
            "CREDIT_CARD" => PiiEntities.CreditCard,
            "EMAIL_ADDRESS" => PiiEntities.Email,
            "IP_ADDRESS" => PiiEntities.IpAddress,
            "IBAN_CODE" => PiiEntities.Iban,
            _ => entity,
        };
        if (entity == "PHONE_NUMBER") return new PhoneNumberRecognizer(regions ?? PhoneNumberRecognizer.DefaultRegions);
        return GlobalRecognizers.Create().Concat(UsRecognizers.Create()).Concat(UkRecognizers.Create()).Single(recognizer => recognizer.Entity == kind);
    }
}
