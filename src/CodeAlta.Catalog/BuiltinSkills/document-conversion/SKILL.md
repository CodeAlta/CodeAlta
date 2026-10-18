---
name: document-conversion
description: Use this skill to read a document that is not plain text - PDF, Word (.docx), Excel (.xlsx), PowerPoint (.pptx), EPUB, RTF, HTML - by converting it to Markdown with MarkItDotNet through `dnx`, when the document is not already in the conversation as text.
---

# Convert a document to Markdown

A model reads text. A PDF, a Word document, a spreadsheet or a presentation is read by converting it to Markdown first, with [MarkItDotNet](https://github.com/elbruno/ElBruno.MarkItDotNet). It runs with `dnx`, the tool runner of the .NET SDK: the first run downloads the pinned version of the tool into the NuGet cache, the next ones start at once. Nothing is installed.

## When to use it

- A file of the project, or a path the user gives, is a document: `.pdf`, `.docx`, `.xlsx`, `.pptx`, `.epub`, `.rtf`, `.html`.
- A file attached to the prompt arrived as a line such as `Attached file: report.docx (…), saved at <path> (a document that could not be converted to text…)`.

A file the user attached to the prompt is usually converted already: its content is in the prompt between `<attached_file>` tags, and the `converted_to_markdown` attribute names the Markdown file, which can be read again or searched. Do not convert it again.

## Convert

```sh
dotnet dnx ElBruno.MarkItDotNet.Cli@0.9.2 --yes -- "<document>" --quiet --output "<document>.md"
```

- `--yes` accepts the download without a question; `--quiet` keeps the status messages out of the output.
- Write the Markdown to a file next to the document, or to a temporary folder when the document's folder must stay unchanged (a project the user did not ask to change): `--output "<temp folder>/<name>.md"`. Without `--output`, the Markdown goes to the standard output.
- Then read the Markdown file. A large document makes a large file: read it in parts, or search it, rather than all at once.
- Exit code `0` is success. `1` is a document that cannot be read (corrupted, protected by a password). `2` is a file that does not exist.
- `dotnet dnx ElBruno.MarkItDotNet.Cli@0.9.2 --yes -- formats` lists the formats this version reads: text, JSON, HTML, Word, PDF, images, CSV and TSV, XML, YAML, RTF, EPUB, URL files, Markdown, Excel, PowerPoint.
- `dotnet dnx ElBruno.MarkItDotNet.Cli@0.9.2 --yes -- batch "<folder>"` converts every file of a folder.

Keep the version `@0.9.2`: a pinned version runs the code that was reviewed, not whatever the latest package is.

## What the conversion keeps

- A PDF gives its text, page by page; a scanned PDF (pictures of pages) gives little or no text. Its pictures, charts and layout are lost: when they matter, say so to the user, and use a model that reads PDFs if one is available.
- A spreadsheet gives a Markdown table per sheet; a presentation, the text of each slide.

## When it does not run

- `dotnet: command not found`, or `dnx` unknown: `dnx` needs the .NET 10 SDK or later. Tell the user; do not install an SDK without asking.
- The download fails: the machine may be offline or have no access to nuget.org. Tell the user what failed.
