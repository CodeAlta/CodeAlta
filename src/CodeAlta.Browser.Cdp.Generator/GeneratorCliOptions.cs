using System.Diagnostics.CodeAnalysis;
using XenoAtom.CommandLine;
using XenoAtom.CommandLine.Terminal;

namespace CodeAlta.Browser.Cdp.Generator;

/// <summary>The options of the generator.</summary>
/// <param name="Revision">The commit of <c>ChromeDevTools/devtools-protocol</c> to download; with a folder, only recorded.</param>
/// <param name="ProtocolDirectory">A folder that holds <c>browser_protocol.json</c> and <c>js_protocol.json</c>, or null to download them.</param>
/// <param name="OutputDirectory">The folder of the generated files, or null for <c>src/CodeAlta.Browser/Cdp/generated</c>.</param>
/// <param name="MethodsFile">The method map, or null for the one of the generator.</param>
internal sealed record GeneratorCliOptions(string? Revision, string? ProtocolDirectory, string? OutputDirectory, string? MethodsFile)
{
    public const string Namespace = "CodeAlta.Browser.Cdp";

    public static CommandApp CreateCommandApp(Func<GeneratorCliOptions, ValueTask<int>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        var state = new ParseState();
        const string _ = "";
        return new CommandApp(
            "CodeAlta.Browser.Cdp.Generator",
            config: new CommandConfig
            {
                OutputFactory = static _ => new TerminalVisualCommandOutput(new TerminalVisualOutputOptions { UseTableForOptions = true, SectionGroupMinWidth = 70, ErrorGroupMinWidth = 70 }),
            })
        {
            new CommandUsage("Usage: {NAME} --revision <commit> | --protocol-dir <dir> [options]"),
            _,
            "Generates the bindings of the Chrome DevTools Protocol that CodeAlta's browser tools use, from the protocol files of ChromeDevTools/devtools-protocol.",
            _,
            "Options:",
            { "revision=", "The {COMMIT} of ChromeDevTools/devtools-protocol whose json/ files are downloaded", value => state.Revision = value },
            { "protocol-dir=", "A {DIR} that holds browser_protocol.json and js_protocol.json, instead of a download", value => state.ProtocolDirectory = value },
            { "o|output=", "The output {DIR} (default: src/CodeAlta.Browser/Cdp/generated)", value => state.Output = value },
            { "methods=", "The method map {FILE} (default: the generator's cdp-methods.json)", value => state.Methods = value },
            new HelpOption(),
            (ctx, _) =>
            {
                if (!TryCreate(state, out var options, out var error))
                {
                    ctx.Error.WriteLine(error);
                    return ValueTask.FromResult(1);
                }

                return execute(options);
            },
        };
    }

    private static bool TryCreate(ParseState state, [NotNullWhen(true)] out GeneratorCliOptions? options, [NotNullWhen(false)] out string? error)
    {
        options = null;
        if (state.Revision is null && state.ProtocolDirectory is null)
        {
            error = "Give --revision, or --protocol-dir.";
            return false;
        }

        options = new GeneratorCliOptions(state.Revision, state.ProtocolDirectory, state.Output, state.Methods);
        error = null;
        return true;
    }

    private sealed class ParseState
    {
        public string? Revision { get; set; }

        public string? ProtocolDirectory { get; set; }

        public string? Output { get; set; }

        public string? Methods { get; set; }
    }
}
