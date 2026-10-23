using System.Text.Json;
using CodeAlta.Agent.Claude;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ClaudeCodeJsonTests
{
    [TestMethod]
    [DataRow(@"""plain""", "plain")]
    [DataRow(@"""a whole pair 😀""", "a whole pair \U0001F600")]
    [DataRow(@"""cut \ud83d""", "cut �")]
    [DataRow(@"""\ud83d then text""", "� then text")]
    [DataRow(@"""a low half \ude00 alone""", "a low half � alone")]
    [DataRow(@"""two highs \ud83d😀""", "two highs �\U0001F600")]
    [DataRow(@"""an escaped backslash \\ud83d""", @"an escaped backslash \ud83d")]
    [DataRow(@"""quote \"" and tab \t é""", "quote \" and tab \t é")]
    public void ReadString_ReplacesEachLoneSurrogateEscape(string json, string expected)
    {
        using var document = JsonDocument.Parse("{\"text\":" + json + "}");

        Assert.AreEqual(expected, ClaudeCodeJson.GetString(document.RootElement, "text"));
        Assert.AreEqual(expected, ClaudeCodeJson.ReadText(document.RootElement.GetProperty("text")));
    }
}
