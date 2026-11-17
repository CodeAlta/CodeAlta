using System.Text.RegularExpressions;
using CodeAlta.Plugin.Pseudonymizer;
using CodeAlta.Pseudonymization;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class PseudonymizerWordsTests
{
    [TestMethod]
    public async Task TheSafeWordsOfAFreshProfile_TheWordsOfCodeAlta_AreNeverHidden()
    {
        // A model takes the name of the application for a place; the email recognizer finds the address of its trailer.
        var places = new PatternRecognizer(PiiEntities.Location, new Regex("CodeAlta|Montauban", RegexOptions.CultureInvariant), 0.8);
        var boundary = new PseudonymizingBoundary(new PiiAnalyzer([.. CodeAlta.Pseudonymization.Presidio.GlobalRecognizers.Create(), places]), new PseudonymTable(), new PseudonymCache(),
            new PseudonymizerOptions { NeverHide = new HashSet<string>(PseudonymizerPlugin.DefaultSafeWords, StringComparer.OrdinalIgnoreCase) });

        var sent = await boundary.PseudonymizeAsync(
            "End every commit with Co-authored-by: CodeAlta <noreply@codealta.io>. Write to sophie.martin@exemple.fr, at Montauban.", CancellationToken.None);

        StringAssert.Contains(sent, "Co-authored-by: CodeAlta <noreply@codealta.io>");
        Assert.IsFalse(sent.Contains("sophie.martin@exemple.fr", StringComparison.Ordinal), "The address of a person is still hidden.");
        Assert.IsFalse(sent.Contains("Montauban", StringComparison.Ordinal), "A place is still hidden.");
    }
}
