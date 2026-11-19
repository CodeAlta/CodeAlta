using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SkillPromptMentionsTests
{
    [TestMethod]
    [DataRow("/grilling", "grilling")]
    [DataRow("/grilling my question", "grilling")]
    [DataRow("first /grilling\nthen (/tdd) and /code-review.", "grilling|tdd|code-review")]
    [DataRow("/plugin:skill, /a", "plugin:skill|a")]
    [DataRow("/grilling then /GRILLING again", "grilling")]
    [DataRow("/tdd: the parser", "tdd")]
    public void Find_NamesAtTheStartOfAWord(string text, string expected)
        => CollectionAssert.AreEqual(expected.Split('|'), SkillPromptMentions.Find(text).ToArray());

    [TestMethod]
    [DataRow("")]
    [DataRow("no skill here")]
    [DataRow("src/app.ts and /usr/bin")]
    [DataRow("https://example.com/grilling")]
    [DataRow("a / b and /-dash and //twice")]
    [DataRow("word/grilling")]
    public void Find_PathsAndOtherSlashesNameNothing(string text)
        => Assert.HasCount(0, SkillPromptMentions.Find(text));

    // Only the plain text of the Markdown names a skill: what the timeline shows as code names none.
    [TestMethod]
    [DataRow("```\nGET /research 200\n```\n/tdd", "tdd")]
    [DataRow("~~~~\n/research\n~~~\n/tdd\n~~~~\n/tdd", "tdd")]
    [DataRow("run `cd /research` then /tdd", "tdd")]
    [DataRow("``a ` /research`` /tdd", "tdd")]
    [DataRow("```\n/research", "")]
    [DataRow("```\n/research\n\n/more", "")]
    [DataRow("` /tdd", "tdd")]
    [DataRow("```js /x\n/research\n```", "")]
    [DataRow("text\n\n    /research indented\n\n/tdd", "tdd")]
    [DataRow("> ```\n> /research\n> ```\n> /tdd", "tdd")]
    [DataRow("> ```\n> /research\n\n/tdd", "tdd")]
    [DataRow("1. step\n   ```\n   /research\n   ```\n   /tdd", "tdd")]
    [DataRow("- item\n\t/tdd\n\n\t\t/research", "tdd")]
    [DataRow("a\r\n```\r\n/research\r\n```\r\n/tdd", "tdd")]
    [DataRow("> quote\nlazy `/research\ncont` /tdd", "tdd")]
    // A backtick alone in two paragraphs opens no code span, and an escaped one neither.
    [DataRow("use ` as quote\n\n/tdd and ` again", "tdd")]
    [DataRow("\\` /tdd \\`", "tdd")]
    public void Find_CodeNamesNoSkill(string text, string expected)
        => CollectionAssert.AreEqual(expected.Length == 0 ? Array.Empty<string>() : expected.Split('|'), SkillPromptMentions.Find(text).ToArray());

    [TestMethod]
    [DataRow("[docs](/research) and [see /research now](http://x) /tdd", "tdd")]
    [DataRow("![alt /research c](/research \"/research\") <https://x/research> /tdd", "tdd")]
    [DataRow("[ref]: /research \"title\"\n\n/tdd [ref]", "tdd")]
    [DataRow("<div>\n/research\n</div>\n\n/tdd <b>/research</b> <a href=\"/research\"> <!-- /research -->", "tdd")]
    [DataRow("**/research** and */research* /tdd", "tdd")]
    public void Find_LinksImagesHtmlAndEmphasisMarksNameNoSkill(string text, string expected)
        => CollectionAssert.AreEqual(expected.Split('|'), SkillPromptMentions.Find(text).ToArray());

    [TestMethod]
    [DataRow("> /tdd\n>> /a\n- /b\n1) /c\n## /e ##\nTitle /f\n---", "tdd|a|b|c|e|f")]
    [DataRow("   /three\n    /lazy four\nline  \n/hard\nline\\\n/slash", "three|lazy|hard|slash")]
    [DataRow("[/tdd] (/a) {/b} a * b /c snake_case /d! a < b /e > c [open /f", "tdd|a|b|c|d|e|f")]
    [DataRow("**bold** /tdd, `code` /a; &copy; /b", "tdd|a|b")]
    public void Find_PlainTextOfBlocksAndBesideMarkupNamesSkills(string text, string expected)
        => CollectionAssert.AreEqual(expected.Split('|'), SkillPromptMentions.Find(text).ToArray());

    [TestMethod]
    public void FindMentions_GivesThePlaceOfEachMentionInThePrompt()
    {
        const string text = "> /tdd then\r\n`/x` (/Code-Review). /tdd";
        var mentions = SkillPromptMentions.FindMentions(text);
        CollectionAssert.AreEqual(new[] { "/tdd", "/Code-Review", "/tdd" }, mentions.Select(mention => text.Substring(mention.Start, mention.Length)).ToArray());
        CollectionAssert.AreEqual(new[] { "tdd", "Code-Review", "tdd" }, mentions.Select(mention => mention.Name).ToArray());
    }

    // A prompt is at most 32,768 characters: the deepest nesting it can hold is read without failing.
    [TestMethod]
    [DataRow(">")]
    [DataRow("- ")]
    [DataRow("[")]
    [DataRow("*")]
    [DataRow("`")]
    [DataRow("<")]
    [DataRow("![")]
    [DataRow("\n> ```\n")]
    public void Find_ReadsAPromptOfRepeatedMarkup(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 32768 / unit.Length)) + " /tdd";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.IsNotNull(SkillPromptMentions.Find(text));
        Assert.IsLessThan(TimeSpan.FromSeconds(5), watch.Elapsed);
    }

    // Letters and decimal digits of any script, beyond the first plane too; a joiner never ends a name.
    [TestMethod]
    [DataRow("/\U00010428\U00010429 and /caf\u00e9 and /\u6280\u80fd", "\U00010428\U00010429|caf\u00e9|\u6280\u80fd")]
    [DataRow("ask\u0085/tdd and\u00a0/a and\u2028/b", "tdd|a|b")]
    [DataRow("/tdd. /a: /b.c, /d..", "tdd|a|b.c|d")]
    public void Find_NamesOfAnyScriptAfterAnyWhiteSpace(string text, string expected)
        => CollectionAssert.AreEqual(expected.Split('|'), SkillPromptMentions.Find(text).ToArray());

    [TestMethod]
    [DataRow("/tdd- /tdd_ /a.b- /x\u00b2 /\ud83d x /.a /a/b /tdd/")]
    [DataRow("\\/tdd and \ufeff/tdd and x/tdd")]
    public void Find_ANameAJoinerOrAnotherCharacterEndsIsNoMention(string text)
        => Assert.HasCount(0, SkillPromptMentions.Find(text));

    [TestMethod]
    public void Find_ANameLongerThanTheLimitIsNoMention()
        => Assert.HasCount(0, SkillPromptMentions.Find("/" + new string('a', SkillPromptMentions.MaximumNameLength + 1)));
}
