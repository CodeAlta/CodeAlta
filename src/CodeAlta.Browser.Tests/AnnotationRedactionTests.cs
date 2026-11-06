using System.Collections.Immutable;
using CodeAlta.Browser.Annotations;

namespace CodeAlta.Browser.Tests;

/// <summary>What leaves a page in the text of an annotation, by the rules the host shares with the toolbar: no browser runs here.</summary>
[TestClass]
public sealed class AnnotationRedactionTests
{
    /// <summary>
    /// Texts with the name of a credential before a value, and what a prompt keeps of them: the value goes, whatever it
    /// looks like. The toolbar gives the same (<see cref="BrowserAnnotationsTests"/>).
    /// </summary>
    internal static ImmutableArray<(string Text, string Redacted)> LabelledCredentials { get; } =
    [
        ("Password: swordfish", "Password: [redacted]"),
        ("api_key=abcdefgh", "api_key=[redacted]"),
        ("Authorization: Bearer xyz", "Authorization: Bearer [redacted]"),
        ("bearer abc.def", "bearer [redacted]"),
        ("Authorization: Basic dXNlcjpwYXNz", "Authorization: Basic [redacted]"),
        ("pwd=letmein and passwd: x", "pwd=[redacted] and passwd: [redacted]"),
        ("token: \"short\"", "token: \"[redacted]\""),
        ("\"secret\":\"abc\"", "\"secret\":\"[redacted]\""),
        ("access-token=a, session=b; cookie: c", "access-token=[redacted], session=[redacted]; cookie: [redacted]"),
        ("auth=hunter", "auth=[redacted]"),
        ("Key: api_key=sk-12345678 here", "Key: api_key=[redacted] here"),
        // A name alone, or a word that only starts like one, is no credential.
        ("Forgot password?", "Forgot password?"),
        ("Author: Jane", "Author: Jane"),
        ("Cookies: accepted", "Cookies: accepted"),
    ];

    [TestMethod]
    public void TheValueAfterTheNameOfACredential_GoesWhateverItLooksLike()
    {
        foreach (var (text, redacted) in LabelledCredentials)
        {
            Assert.AreEqual(redacted, AnnotationRedaction.Text(text), text);
            // The text between the tags of the HTML of an element follows the same rule.
            Assert.AreEqual("<p>" + redacted + "</p>", AnnotationRedaction.Html("<p>" + text + "</p>", 1000), text);
        }
    }
}
