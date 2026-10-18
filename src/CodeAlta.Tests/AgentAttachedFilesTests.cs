using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentAttachedFilesTests
{
    [TestMethod]
    public void FindMediaType_ReadsTheContentNotTheName()
    {
        Assert.AreEqual("application/pdf", AgentAttachedFiles.FindMediaType("%PDF-1.7\n"u8, "notes.txt"));
        Assert.AreEqual("image/png", AgentAttachedFiles.FindMediaType([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0], "x.bin"));
        Assert.AreEqual("application/vnd.openxmlformats-officedocument.wordprocessingml.document", AgentAttachedFiles.FindMediaType([0x50, 0x4B, 0x03, 0x04, 0], "Plan.DOCX"));
        Assert.AreEqual("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", AgentAttachedFiles.FindMediaType([0x50, 0x4B, 0x03, 0x04, 0], "data.xlsx"));
        Assert.AreEqual("application/rtf", AgentAttachedFiles.FindMediaType("{\\rtf1 hello}"u8, "letter.rtf"));
        // A ZIP that is not named as a document, and a document name on content that is not one.
        Assert.AreEqual("application/octet-stream", AgentAttachedFiles.FindMediaType([0x50, 0x4B, 0x03, 0x04, 0], "archive.zip"));
        Assert.AreEqual("text/plain", AgentAttachedFiles.FindMediaType("not a document"u8, "plan.docx"));
        Assert.AreEqual("text/plain", AgentAttachedFiles.FindMediaType("﻿café\r\n\tnaïve"u8.ToArray(), "notes"));
        Assert.AreEqual("text/plain", AgentAttachedFiles.FindMediaType([], "empty.txt"));
        Assert.AreEqual("application/octet-stream", AgentAttachedFiles.FindMediaType([0x61, 0x00, 0x62], "nul.txt"));
        Assert.AreEqual("application/octet-stream", AgentAttachedFiles.FindMediaType([0xC3, 0x28], "invalid-utf8.txt"));
        Assert.AreEqual("application/octet-stream", AgentAttachedFiles.FindMediaType([1, 2, 3, 4, 0x61], "control.txt"));
    }

    [TestMethod]
    public void Map_GivesATextFileItsContentAndCutsALongOne()
    {
        using var folder = new TempFolder();
        var path = folder.Write("copy.txt", "line one\nline two");
        var part = (AgentMessagePart.Text)AgentAttachedFiles.Map(new AgentInputItem.LocalFile(path, "notes \"v2\".txt", "text/plain"));
        Assert.AreEqual($"<attached_file name=\"notes &quot;v2&quot;.txt\" path=\"{path}\">\nline one\nline two\n</attached_file>", part.Value);

        var longPath = folder.Write("long.txt", new string('a', AgentAttachedFiles.MaximumInlineCharacters + 10));
        var cut = ((AgentMessagePart.Text)AgentAttachedFiles.Map(new AgentInputItem.LocalFile(longPath, "long.txt", "text/plain"))).Value;
        StringAssert.Contains(cut, $"[The file goes on: read the rest from {longPath}.]");
        StringAssert.Contains(cut, ">\n" + new string('a', AgentAttachedFiles.MaximumInlineCharacters) + "\n[The file goes on");
    }

    [TestMethod]
    public void Map_GivesADocumentItsMarkdownAndAnyOtherFileItsPath()
    {
        using var folder = new TempFolder();
        var document = folder.Write("plan.docx", "PK");
        const string word = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        var unconverted = ((AgentMessagePart.Text)AgentAttachedFiles.Map(new AgentInputItem.LocalFile(document, "plan.docx", word))).Value;
        StringAssert.Contains(unconverted, $"saved at {document}");
        StringAssert.Contains(unconverted, "document-conversion skill");

        File.WriteAllText(AgentAttachedFiles.ConvertedPath(document), "# Plan");
        var converted = ((AgentMessagePart.Text)AgentAttachedFiles.Map(new AgentInputItem.LocalFile(document, "plan.docx", word))).Value;
        Assert.AreEqual($"<attached_file name=\"plan.docx\" path=\"{document}\" converted_to_markdown=\"{document}.md\">\n# Plan\n</attached_file>", converted);

        var binary = folder.Write("data.bin", "\0");
        var other = ((AgentMessagePart.Text)AgentAttachedFiles.Map(new AgentInputItem.LocalFile(binary, "data.bin", "application/octet-stream"))).Value;
        Assert.AreEqual($"Attached file: data.bin (application/octet-stream), saved at {binary} (not text: open it with your tools).", other);

        var missing = ((AgentMessagePart.Text)AgentAttachedFiles.Map(new AgentInputItem.LocalFile(Path.Combine(folder.Root, "gone.txt"), "gone.txt", "text/plain"))).Value;
        StringAssert.Contains(missing, "the file no longer exists");
        Assert.AreEqual($"Attached file (plan.docx): {document}", AgentAttachedFiles.RenderUserInput(new AgentInputItem.LocalFile(document, "plan.docx", word)));
    }

    [TestMethod]
    public void Pdf_IsTheDocumentForAModelThatReadsPdfsAndItsMarkdownForAnother()
    {
        using var folder = new TempFolder();
        var pdf = folder.Write("copy.pdf", "%PDF-1.4\n%%EOF\n");
        var reference = AgentAttachedFiles.Map(new AgentInputItem.LocalFile(pdf, "report.pdf", "application/pdf"));
        Assert.AreEqual(new AgentMessagePart.Uri(pdf, "application/pdf", "report.pdf"), reference);
        AgentConversationMessage[] conversation = [new(AgentConversationRole.User, [reference])];
        var reader = new AgentModelInfo("reader", Capabilities: new Dictionary<string, object?> { [AgentPdfInputs.CapabilityKey] = true });
        var text = new AgentModelInfo("text");

        var document = (AgentMessagePart.Data)AgentPdfInputs.Resolve(conversation, reader)[0].Parts[0];
        Assert.AreEqual("application/pdf", document.MediaType);
        Assert.AreEqual("report.pdf", document.Name);
        Assert.AreEqual(new AgentMessagePart.Text($"File: {pdf}"), AgentPdfInputs.Resolve(conversation, text)[0].Parts[0]);

        File.WriteAllText(AgentAttachedFiles.ConvertedPath(pdf), "The secret word is PELICAN.");
        var markdown = (AgentMessagePart.Text)AgentPdfInputs.Resolve(conversation, text)[0].Parts[0];
        Assert.AreEqual($"<attached_file name=\"report.pdf\" path=\"{pdf}\" converted_to_markdown=\"{pdf}.md\">\nThe secret word is PELICAN.\n</attached_file>", markdown.Value);
        Assert.IsInstanceOfType<AgentMessagePart.Data>(AgentPdfInputs.Resolve(conversation, reader)[0].Parts[0], "A model that reads PDFs still gets the document.");

        // A PDF of the project named with @ is not an attached copy: what is beside it is not its conversion.
        var project = Path.Combine(Path.GetDirectoryName(folder.Root)!, "docs");
        Directory.CreateDirectory(project);
        var named = Path.Combine(project, "spec.pdf");
        File.WriteAllText(named, "%PDF-1.4\n");
        File.WriteAllText(AgentAttachedFiles.ConvertedPath(named), "not a conversion");
        AgentConversationMessage[] referenced = [new(AgentConversationRole.User, [new AgentMessagePart.Uri(named, "application/pdf", "spec.pdf")])];
        Assert.AreEqual(new AgentMessagePart.Text($"File: {named}"), AgentPdfInputs.Resolve(referenced, text)[0].Parts[0]);
    }

    [TestMethod]
    public void Extension_IsTheOneOfTheTypeForAPdfOrADocument()
    {
        Assert.AreEqual(".pdf", AgentAttachedFiles.Extension("application/pdf"));
        Assert.AreEqual(".xlsx", AgentAttachedFiles.Extension("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        Assert.IsNull(AgentAttachedFiles.Extension("text/plain"));
        Assert.IsTrue(AgentAttachedFiles.IsAttachedCopy("/home/x/.alta/sessions/2026/10/18/s.attachments/a.pdf"));
        Assert.IsFalse(AgentAttachedFiles.IsAttachedCopy("/home/x/project/a.pdf"));
    }

    private sealed class TempFolder : IDisposable
    {
        // Like the attachment folder of a session.
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Directory.CreateTempSubdirectory("codealta-attached-files-").FullName, "session.attachments")).FullName;

        public string Write(string name, string content)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public void Dispose() => Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true);
    }
}
