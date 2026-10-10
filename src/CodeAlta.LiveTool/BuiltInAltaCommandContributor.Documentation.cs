using System.Globalization;
using System.Text.RegularExpressions;
using CodeAlta.Catalog.Documentation;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta documentation` commands: the user guide that ships with the application. Listing, reading and searching
// need no window; showing a page to the user does.
internal sealed partial class BuiltInAltaCommandContributor
{
    // The characters of a page that one answer carries, so that it stays below the output limit of a session.
    private const int DocumentationReadLimit = 24_000;
    private const int DocumentationMaximumReadLimit = 48_000;

    private static readonly AltaCommandPolicy[] DocumentationPolicies =
    [
        Read("documentation list", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Read("documentation read", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Read("documentation search", requiresRuntime: false, supportsCatalogOnlyContext: true),
    ];

    // It changes nothing but what the window shows.
    private static readonly AltaCommandPolicy DocumentationOpenPolicy = Read("documentation open");

    private static Command CreateDocumentationCommand(AltaCommandContext context)
    {
        var group = Group("documentation", "Read the user guide of CodeAlta that ships with the application, and show a page of it to the user.");
        group.Add(CreateDocumentationListCommand(context));
        group.Add(CreateDocumentationReadCommand(context));
        group.Add(CreateDocumentationSearchCommand(context));
        if (context.Services.Get<IAltaDocumentationView>() is not null)
        {
            group.Add(CreateDocumentationOpenCommand(context));
        }

        AddHelpText(
            group,
            "The guide says what CodeAlta does and where things are in its window: answer a question about CodeAlta from it.",
            "<page> is the path of a page below the folder of the guide, with forward slashes, as `alta documentation list` prints it: `getting-started.md`, `plugins/statistics.md`. A link of a page names another page the same way, whatever the page that links to it.",
            "Examples: `alta documentation list`; `alta documentation search worktree`; `alta documentation read worktrees.md`; `alta documentation open worktrees.md`.");
        return group;
    }

    private static Command CreateDocumentationListCommand(AltaCommandContext context)
    {
        var command = Leaf("list", "List the pages of the user guide, in the order of its menu.");
        command.Add((_, _) => ValueTask.FromResult(HandleDocumentationList(context)));
        AddHelpText(command,
            "`depth` is 0 for an entry of the menu and 1 for an entry of the menu of a folder. A page the menu does not name is listed last, with `menu: false`.",
            "Example: `alta documentation list`.");
        return command;
    }

    private static Command CreateDocumentationReadCommand(AltaCommandContext context)
    {
        string? page = null, offset = null, limit = null;
        var command = Leaf("read", "Print a page of the user guide as Markdown.");
        command.Add("<page>?", "The page, as `alta documentation list` prints it. Without it, the first page of the guide.", value => page = value);
        command.Add("offset=", "The character of the page to start at, as `nextOffset` of the answer before gave it.", value => offset = value);
        command.Add("limit=", $"The most characters to print: {DocumentationReadLimit.ToString(CultureInfo.InvariantCulture)} when left out, {DocumentationMaximumReadLimit.ToString(CultureInfo.InvariantCulture)} at most.", value => limit = value);
        command.Add((_, _) => ValueTask.FromResult(HandleDocumentationRead(context, page, offset, limit)));
        AddHelpText(command,
            "The Markdown is the page as the application shows it: the templates of the site are replaced, and a picture is an image below the folder of the guide (`root`), followed by its caption.",
            "A long page comes in parts: when `truncated` is true, read on with `--offset <nextOffset>`.",
            "Examples: `alta documentation read`; `alta documentation read sessions.md`; `alta documentation read workspace.md --offset 24000`.");
        return command;
    }

    private static Command CreateDocumentationSearchCommand(AltaCommandContext context)
    {
        string? text = null;
        var command = Leaf("search", "Find a text in the pages of the user guide.");
        command.Add("<text>", "The text to find, whatever its case: two characters at least.", value => text = value);
        command.Add((_, _) => ValueTask.FromResult(HandleDocumentationSearch(context, text)));
        AddHelpText(command,
            "Each place names its page, the heading it is under and the line that has the text. Quote a text of several words.",
            "Examples: `alta documentation search worktree`; `alta documentation search \"default provider\"`.");
        return command;
    }

    private static Command CreateDocumentationOpenCommand(AltaCommandContext context)
    {
        string? page = null, anchor = null;
        var command = Leaf("open", "Show the user guide in the CodeAlta window, at a page of it.");
        command.Add("<page>?", "The page, as `alta documentation list` prints it. Without it, the page the window shows already, or the first page.", value => page = value);
        command.Add("anchor=", "The heading of the page to go to: its text in lower case, with a dash for each space, as a link of the guide writes it after `#`.", value => anchor = value);
        command.Add((_, _) => ValueTask.FromResult(HandleDocumentationOpen(context, page, anchor)));
        AddHelpText(command,
            "It only shows the page to the user: to read a page yourself, use `alta documentation read`.",
            "Examples: `alta documentation open`; `alta documentation open sessions.md`; `alta documentation open workspace.md --anchor code-editor`.");
        return command;
    }

    private static int HandleDocumentationList(AltaCommandContext context)
    {
        if (!TryGetDocumentation(context, out var documentation))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var count = 0;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Write(ShippedDocumentationMenuItem item, int depth, string? parent)
        {
            listed.Add(item.Path);
            count++;
            WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "alta.documentation.page",
                ["version"] = 1,
                ["correlationId"] = context.CorrelationId,
                ["page"] = item.Path,
                ["title"] = item.Title,
                ["depth"] = depth,
                ["parent"] = parent,
                ["menu"] = true,
            });
            foreach (var child in item.Children)
            {
                Write(child, depth + 1, item.Path);
            }
        }

        foreach (var item in documentation.GetMenu())
        {
            Write(item, 0, null);
        }

        foreach (var page in documentation.ListPages())
        {
            if (!listed.Add(page.Path))
            {
                continue;
            }

            count++;
            WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "alta.documentation.page",
                ["version"] = 1,
                ["correlationId"] = context.CorrelationId,
                ["page"] = page.Path,
                ["title"] = page.Title,
                ["depth"] = 0,
                ["menu"] = false,
            });
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.documentation.summary",
            version = 1,
            correlationId = context.CorrelationId,
            count,
            truncated = false,
            root = documentation.Root,
            home = documentation.Home,
        });
        return AltaExitCodes.Success;
    }

    private static int HandleDocumentationRead(AltaCommandContext context, string? pageRef, string? offsetText, string? limitText)
    {
        const string CommandPath = "alta documentation read";
        if (!TryGetDocumentation(context, out var documentation))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var offset = 0;
        if (NormalizeOptionalText(offsetText) is { } offsetValue
            && (!int.TryParse(offsetValue, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
        {
            return UsageError(context, "usage.invalidOffset", "The offset must be a number of characters, 0 or more.", CommandPath);
        }

        var limit = DocumentationReadLimit;
        if (NormalizeOptionalText(limitText) is { } limitValue
            && (!int.TryParse(limitValue, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > DocumentationMaximumReadLimit))
        {
            return UsageError(context, "usage.invalidLimit", $"The limit must be a number of characters, from 1 to {DocumentationMaximumReadLimit.ToString(CultureInfo.InvariantCulture)}.", CommandPath);
        }

        var (page, exitCode) = ResolveDocumentationPage(context, documentation, pageRef, fallbackToHome: true);
        if (page is null)
        {
            return exitCode;
        }

        if (documentation.ReadPage(page) is not { } read)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "documentation.unreadable", AltaExitCodes.Failure, $"The page '{page}' could not be read.");
            return AltaExitCodes.Failure;
        }

        var markdown = read.ToMarkdown();
        if (offset > markdown.Length)
        {
            return UsageError(context, "usage.invalidOffset", $"The page has {markdown.Length.ToString(CultureInfo.InvariantCulture)} characters: the offset is past its end.", CommandPath);
        }

        var end = Math.Min(markdown.Length, offset + limit);
        // A part ends with a line, and never between the two halves of a character.
        if (end < markdown.Length)
        {
            var line = markdown.LastIndexOf('\n', end - 1, end - offset);
            if (line > offset)
            {
                end = line + 1;
            }
            else if (char.IsHighSurrogate(markdown[end - 1]))
            {
                end--;
            }
        }

        var truncated = end < markdown.Length;
        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.documentation.content",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["page"] = read.Path,
            ["title"] = read.Title,
            ["root"] = documentation.Root,
            ["file"] = documentation.GetPageFile(read.Path),
            ["length"] = markdown.Length,
            ["offset"] = offset,
            ["truncated"] = truncated,
            ["nextOffset"] = truncated ? end : null,
            ["markdown"] = markdown[offset..end],
        });
        return AltaExitCodes.Success;
    }

    private static int HandleDocumentationSearch(AltaCommandContext context, string? text)
    {
        if (!TryGetDocumentation(context, out var documentation))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(text) is not { Length: >= 2 and <= 100 } query)
        {
            return UsageError(context, "usage.invalidText", "A text to find is required: from 2 to 100 characters.", "alta documentation search");
        }

        var hits = documentation.Search(query);
        foreach (var hit in hits)
        {
            WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "alta.documentation.hit",
                ["version"] = 1,
                ["correlationId"] = context.CorrelationId,
                ["page"] = hit.Path,
                ["title"] = hit.Title,
                ["heading"] = hit.Heading,
                ["text"] = hit.Text,
            });
        }

        WriteSummary(context, "alta.documentation.hitSummary", hits.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int HandleDocumentationOpen(AltaCommandContext context, string? pageRef, string? anchorRef)
    {
        const string CommandPath = "alta documentation open";
        if (!TryGetDocumentation(context, out var documentation))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (context.Services.Get<IAltaDocumentationView>() is not { } view)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
                "Required in-process service 'IAltaDocumentationView' is unavailable.");
            return AltaExitCodes.ServiceUnavailable;
        }

        var (page, exitCode) = ResolveDocumentationPage(context, documentation, pageRef, fallbackToHome: false);
        if (page is null && exitCode != AltaExitCodes.Success)
        {
            return exitCode;
        }

        var anchor = NormalizeOptionalText(anchorRef)?.TrimStart('#');
        if (anchor is not null && (page is null || !DocumentationAnchor().IsMatch(anchor)))
        {
            return UsageError(context, "usage.invalidAnchor",
                page is null ? "An anchor names a heading of a page: name the page too." : "The anchor is the address of a heading: letters, digits, dashes and underscores, as a link of the guide writes it after `#`.", CommandPath);
        }

        if (!view.Show(page, anchor))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "view.unavailable", AltaExitCodes.ServiceUnavailable,
                "No CodeAlta window is open to show the user guide.");
            return AltaExitCodes.ServiceUnavailable;
        }

        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.documentation.opened",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["page"] = page,
            ["anchor"] = anchor,
        });
        return AltaExitCodes.Success;
    }

    // The page a caller names, as the guide writes it. A full path of a file of the guide names its page too: it is what a reader of the files has.
    private static (string? Page, int ExitCode) ResolveDocumentationPage(AltaCommandContext context, ShippedDocumentation documentation, string? reference, bool fallbackToHome)
    {
        if (NormalizeOptionalText(reference) is not { } text)
        {
            if (!fallbackToHome)
            {
                return (null, AltaExitCodes.Success);
            }

            return documentation.Home is { } home
                ? (home, AltaExitCodes.Success)
                : (null, NotFound(context, "documentation.notFound", "The user guide has no page."));
        }

        var page = documentation.ResolvePage(text.Replace('\\', '/')) ?? documentation.FindPage(text);
        return page is not null
            ? (page, AltaExitCodes.Success)
            : (null, NotFound(context, "documentation.notFound", $"The user guide has no page '{text}'. List its pages with `alta documentation list`."));
    }

    private static bool TryGetDocumentation(AltaCommandContext context, out ShippedDocumentation documentation)
    {
        documentation = context.Services.Get<ShippedDocumentation>()!;
        if (documentation is null)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
                "Required in-process service 'ShippedDocumentation' is unavailable.");
            return false;
        }

        if (documentation.Available)
        {
            return true;
        }

        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "documentation.unavailable", AltaExitCodes.ServiceUnavailable,
            "This application ships no user guide.");
        return false;
    }

    [GeneratedRegex(@"^[\p{L}\p{N}_-]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentationAnchor();
}
