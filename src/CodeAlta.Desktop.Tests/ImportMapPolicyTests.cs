using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The entry document of the page has an inline import map, which gives the libraries lent to plugins their names. The content
/// security policy of the page (<c>assets.csp</c> of <c>neoastra.json</c>) allows no inline script but that one, by the hash of its
/// text: a map that changes without the policy is refused by the page, and no module of a plugin loads.
/// </summary>
[TestClass]
public sealed partial class ImportMapPolicyTests
{
    [TestMethod]
    public void TheImportMapOfTheBuiltDocument_IsTheOneThePolicyOfThePageAllows()
    {
        var application = Path.Combine(DesktopArchitectureTests.SourceRoot, "CodeAlta");
        var document = Path.Combine(application, "frontend", "dist", "index.html");
        // A build that skips the frontend (a loop on the C# tests alone) may have no document at all.
        if (!File.Exists(document)) Assert.Inconclusive("The frontend was not built: there is no entry document to check.");

        var maps = ImportMap().Matches(File.ReadAllText(document));
        Assert.AreEqual(1, maps.Count, "the entry document has one inline import map");
        var hash = $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(maps[0].Groups[1].Value)))}'";

        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(application, "neoastra.json")));
        var policy = configuration.RootElement.GetProperty("assets").GetProperty("csp").GetString()!;
        var script = policy.Split(';', StringSplitOptions.TrimEntries).Single(static directive => directive.StartsWith("script-src ", StringComparison.Ordinal));
        Assert.AreEqual($"script-src 'self' {hash}", script, "the script policy is the origin and the hash of the import map of the document, nothing else");
    }

    [GeneratedRegex("<script type=\"importmap\">([^<]*)</script>")]
    private static partial Regex ImportMap();
}
