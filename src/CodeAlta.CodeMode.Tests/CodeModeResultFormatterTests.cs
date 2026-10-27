using System.Text.Json;

namespace CodeAlta.CodeMode.Tests;

[TestClass]
public sealed class CodeModeResultFormatterTests
{
    private static CodeModeRunResult Completed(object? value, params string[] output) => new(CodeModeRunStatus.Completed, TimeSpan.FromSeconds(2))
    {
        ValueJson = value is null ? null : JsonSerializer.Serialize(value),
        Output = [.. output.Select(static line => new CodeModeOutputEntry(CodeModeOutputKind.Log, line, null))],
    };

    [TestMethod]
    public void AText_IsTheResultAsItIs_LastAfterTheOutput()
    {
        var text = CodeModeResultFormatter.Format(Completed("### Fix\n- `alta/branch` done\n- \"quoted\" ≈ ok", "checked"));

        Assert.AreEqual("Script completed · 2.0 s · no calls\nOutput:\n```\nchecked\n```\nResult:\n### Fix\n- `alta/branch` done\n- \"quoted\" ≈ ok\n", text);
    }

    [TestMethod]
    public void TheLinesOfTheHost_GoBeforeTheResult()
    {
        var text = CodeModeResultFormatter.Format(Completed("done", "checked"), CodeModeResultFormatter.DefaultMaxOutputTokens, null,
            ["Images: 1 attached (Chart)", "image skipped: C:\\out\\a.txt: not a PNG, JPEG, GIF or WebP picture."]);

        Assert.AreEqual("Script completed · 2.0 s · no calls\nOutput:\n```\nchecked\n```\nImages: 1 attached (Chart)\nimage skipped: C:\\out\\a.txt: not a PNG, JPEG, GIF or WebP picture.\nResult:\ndone\n", text);
    }

    [TestMethod]
    public void TheAnswersOfSubAgents_AreSectionsOfMarkdown()
    {
        var presented = CodeModeResultFormatter.Present(JsonSerializer.Serialize(new[] { "## One\nfirst\n", "## Two\nsecond" }));

        Assert.AreEqual(CodeModeResultKind.Markdown, presented.Kind);
        Assert.AreEqual("## One\nfirst\n\n---\n\n## Two\nsecond", presented.Text);
        Assert.AreEqual(CodeModeResultKind.Json, CodeModeResultFormatter.Present("""["a.cs","b.cs"]""").Kind, "short names stay data");
    }

    [TestMethod]
    public void AnObject_IsABlockOfJson_AndAScalarIsItself()
    {
        StringAssert.EndsWith(CodeModeResultFormatter.Format(Completed(new { files = 3 })), "Result:\n```json\n{\"files\":3}\n```\n");
        StringAssert.EndsWith(CodeModeResultFormatter.Format(Completed(42)), "Result:\n42\n");
    }

    [TestMethod]
    public void ALongText_KeepsItsStartAndEnd_AndNamesTheFileOfTheWhole()
    {
        var report = string.Join('\n', Enumerable.Range(1, 2_000).Select(static i => $"- line {i} with `code`"));
        var result = Completed(report);

        var kept = CodeModeResultFormatter.ResultToKeep(result, 1_000);
        var text = CodeModeResultFormatter.Format(result, 1_000, @"C:\session\tool-output\script-run-1-result.md");

        Assert.IsNotNull(kept);
        Assert.AreEqual(report, kept.Text);
        Assert.AreEqual(".md", kept.FileExtension);
        StringAssert.Contains(text, $"Full result: C:\\session\\tool-output\\script-run-1-result.md ({report.Length:N0} characters; a preview of its start and its end is below, read the file for the rest)\nResult:\n- line 1 with `code`\n");
        StringAssert.Contains(text, "characters left out: they are in the full result …");
        StringAssert.EndsWith(text, "- line 2000 with `code`\n");
        Assert.IsLessThan(CodeModeResultFormatter.PreviewCharacters + 400, text.Length);
        Assert.IsFalse(text.Contains("\\u0060", StringComparison.Ordinal));
        Assert.IsNull(CodeModeResultFormatter.ResultToKeep(Completed("short"), 1_000));
    }

    [TestMethod]
    public void AResultKeptInAFile_IsAShortPreview_WhateverTheBudget_AndWithoutAFileTheBudgetIsTheExcerpt()
    {
        var report = string.Join('\n', Enumerable.Range(1, 5_000).Select(static i => $"- line {i}"));
        var result = Completed(report);

        var preview = CodeModeResultFormatter.Format(result, CodeModeResultFormatter.DefaultMaxOutputTokens, @"C:\s\script-run-1-result.md");
        var excerpt = CodeModeResultFormatter.Format(result, CodeModeResultFormatter.DefaultMaxOutputTokens, null);

        Assert.IsLessThan(CodeModeResultFormatter.PreviewCharacters + 400, preview.Length, "about 2,000 characters of the start and 1,000 of the end");
        StringAssert.EndsWith(preview, "- line 5000\n");
        Assert.IsGreaterThan(CodeModeResultFormatter.DefaultMaxOutputTokens * 3, excerpt.Length, "nothing else keeps the result: the excerpt has the budget");
        Assert.IsFalse(excerpt.Contains(CodeModeResultFormatter.FullResultPrefix, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ACutBlockOfCode_IsClosedAndOpenedAgain()
    {
        var code = "intro\n```csharp\n" + string.Join('\n', Enumerable.Range(1, 400).Select(static i => $"var x{i} = {i};")) + "\n```\nend";

        var excerpt = CodeModeMarkdownExcerpt.Cut(code, 1_200, null);

        var fences = excerpt.Split('\n').Count(static line => line.StartsWith("```", StringComparison.Ordinal));
        Assert.AreEqual(0, fences % 2, excerpt);
        StringAssert.Contains(excerpt, "\n```\n\n… ");
        StringAssert.Contains(excerpt, "characters left out …\n\n```csharp\n");
        StringAssert.EndsWith(excerpt, "```\nend");
    }

    [TestMethod]
    public void ALongObject_KeepsItsShape_WithoutEscapes()
    {
        var value = new { notes = string.Concat(Enumerable.Repeat("`tick` é ", 2_000)) };

        var text = CodeModeResultFormatter.Format(Completed(value), 1_000, @"C:\out\script-run-2-result.json");

        StringAssert.Contains(text, "Full result: C:\\out\\script-run-2-result.json (");
        StringAssert.Contains(text, "Result:\n```json\n{\"notes\":\"`tick` é ");
        StringAssert.EndsWith(text, "\n```\n");
        Assert.IsFalse(text.Contains("\\u0060", StringComparison.Ordinal));
        Assert.AreEqual(".json", CodeModeResultFormatter.ResultToKeep(Completed(value), 1_000)!.FileExtension);
    }

    [TestMethod]
    public void OutputWithBackticks_HasALongerFence()
    {
        var text = CodeModeResultFormatter.Format(Completed("done", "```js", "x", "```"));

        StringAssert.Contains(text, "Output:\n````\n```js\nx\n```\n````\nResult:\ndone\n");
    }
}
