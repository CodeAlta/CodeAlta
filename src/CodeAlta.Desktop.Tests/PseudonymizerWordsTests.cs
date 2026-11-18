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

    [TestMethod]
    public async Task TheTechnologyDefaults_AreSent_UnlessTheProjectAlwaysHidesOne()
    {
        var organizations = new PatternRecognizer(PiiEntities.Organization,
            new Regex("CodeAlta Desktop|CUDA|ROCm|Vulkan|EPUB|MarkItDotNet|Unicode|PowerPoint|Metal|CI|io", RegexOptions.CultureInvariant), 0.8);
        var never = new HashSet<string>(PseudonymizerPlugin.DefaultSafeWords, StringComparer.OrdinalIgnoreCase);
        const string text = "CodeAlta Desktop: CUDA, ROCm, Vulkan, Metal, EPUB, MarkItDotNet, Unicode, CI, PowerPoint, io.";

        var boundary = new PseudonymizingBoundary(new PiiAnalyzer([organizations]), new PseudonymTable(), new PseudonymCache(), new PseudonymizerOptions { NeverHide = never });
        Assert.AreEqual(text, await boundary.PseudonymizeAsync(text, CancellationToken.None));

        // A party of the file named as one of them is listed by the project, and hidden.
        var withParty = new PseudonymizingBoundary(new PiiAnalyzer([organizations]), new PseudonymTable(), new PseudonymCache(),
            new PseudonymizerOptions { NeverHide = never, AlwaysHide = [(PiiEntities.Organization, "Metal")] });
        Assert.AreEqual(text.Replace("Metal", "[ORGANIZATION_001]", StringComparison.Ordinal), await withParty.PseudonymizeAsync(text, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("lieu: Montauban", "Montauban", "[LOCATION_001]")]
    [DataRow("ADRESSE: 7 rue des Lilas", "7 rue des Lilas", "[ADDRESS_001]")]
    public async Task AProject_CanStillExplicitlyHideValuesWithAnOldKindPrefix(string setting, string value, string token)
    {
        var configured = PseudonymizerPlugin.ParseValue(setting);
        Assert.IsNotNull(configured);
        var boundary = new PseudonymizingBoundary(new PiiAnalyzer([]), new PseudonymTable(), new PseudonymCache(),
            new PseudonymizerOptions { AlwaysHide = [configured.Value] });

        Assert.AreEqual($"The value is {token}.", await boundary.PseudonymizeAsync($"The value is {value}.", CancellationToken.None));
    }
}
