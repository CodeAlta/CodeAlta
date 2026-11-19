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

    // The same cases as "code names no skill" of skillMentions.test.ts: the host and the page read code alike.
    [TestMethod]
    [DataRow("```\nGET /research 200\n```\n/tdd", "tdd")]
    [DataRow("~~~~\n/research\n~~~\n/tdd\n~~~~\n/tdd", "tdd")]
    [DataRow("run `cd /research` then /tdd", "tdd")]
    [DataRow("``a ` /research`` /tdd", "tdd")]
    [DataRow("```\n/research", "")]
    [DataRow("` /tdd", "tdd")]
    [DataRow("```js /x\n/research\n```", "")]
    public void Find_CodeNamesNoSkill(string text, string expected)
        => CollectionAssert.AreEqual(expected.Length == 0 ? Array.Empty<string>() : expected.Split('|'), SkillPromptMentions.Find(text).ToArray());

    [TestMethod]
    public void Find_ANameLongerThanTheLimitIsNoMention()
        => Assert.HasCount(0, SkillPromptMentions.Find("/" + new string('a', SkillPromptMentions.MaximumNameLength + 1)));
}
