using System.Diagnostics.CodeAnalysis;
using XenoAtom.CommandLine;
using XenoAtom.CommandLine.Terminal;

namespace CodeAlta.CodexAppServer.Generator;

/// <summary>The options of the generator.</summary>
/// <param name="CodexRepositoryDirectory">A clone of <c>openai/codex</c>, read at <paramref name="SourceRef"/>; or <see langword="null"/>.</param>
/// <param name="SourceRef">The tag of the pinned release, such as <c>rust-v0.165.0</c>; or <see langword="null"/>.</param>
/// <param name="SchemaDirectory">A folder of schema files to read as it is, instead of a repository; or <see langword="null"/>.</param>
/// <param name="OutputDirectory">The folder of the generated files, or <see langword="null"/> for <c>src/CodeAlta.CodexAppServer/generated</c>.</param>
/// <param name="MethodsFile">The method map, or <see langword="null"/> for the one of the generator.</param>
internal sealed record GeneratorCliOptions(
    string? CodexRepositoryDirectory,
    string? SourceRef,
    string? SchemaDirectory,
    string? OutputDirectory,
    string? MethodsFile)
{
    public const string ClientNamespace = "CodeAlta.CodexAppServer";
    public const string ProtocolNamespace = "CodeAlta.CodexAppServer.Protocol";
    public const string ContextName = "CodexProtocolJsonContext";

    public static CommandApp CreateCommandApp(Func<GeneratorCliOptions, ValueTask<int>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        var state = new ParseState();
        const string _ = "";
        return new CommandApp(
            "CodeAlta.CodexAppServer.Generator",
            config: new CommandConfig
            {
                OutputFactory = static _ => new TerminalVisualCommandOutput(new TerminalVisualOutputOptions
                {
                    UseTableForOptions = true,
                    SectionGroupMinWidth = 70,
                    ErrorGroupMinWidth = 70,
                }),
            })
        {
            new CommandUsage("Usage: {NAME} --codex-repo-dir <dir> --source-ref <tag> | --schema-dir <dir> [options]"),
            _,
            "Generates the types of the Codex app-server protocol CodeAlta uses, from the schema Codex checks into its repository.",
            _,
            "Options:",
            { "codex-repo-dir=", "A clone of openai/codex {DIR}: its schema is read at --source-ref, without a checkout", value => state.Repository = value },
            { "source-ref=", "The {TAG} of the pinned Codex release, such as rust-v0.165.0", value => state.SourceRef = value },
            { "schema-dir=", "A {DIR} of schema files to read as it is, instead of a repository", value => state.SchemaDirectory = value },
            { "o|output=", "The output {DIR} (default: src/CodeAlta.CodexAppServer/generated)", value => state.Output = value },
            { "methods=", "The method map {FILE} (default: the generator's codex-app-server-methods.json)", value => state.Methods = value },
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
        if (state.SchemaDirectory is null && (state.Repository is null || state.SourceRef is null))
        {
            error = "Give --codex-repo-dir with --source-ref, or --schema-dir.";
            return false;
        }

        if (state.SchemaDirectory is not null && state.Repository is not null)
        {
            error = "Give --codex-repo-dir or --schema-dir, not both.";
            return false;
        }

        options = new GeneratorCliOptions(state.Repository, state.SourceRef, state.SchemaDirectory, state.Output, state.Methods);
        error = null;
        return true;
    }

    private sealed class ParseState
    {
        public string? Repository { get; set; }

        public string? SourceRef { get; set; }

        public string? SchemaDirectory { get; set; }

        public string? Output { get; set; }

        public string? Methods { get; set; }
    }
}
