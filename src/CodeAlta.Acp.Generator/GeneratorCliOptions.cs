using System.Diagnostics.CodeAnalysis;
using XenoAtom.CommandLine;
using XenoAtom.CommandLine.Terminal;

namespace CodeAlta.Acp.Generator;

internal sealed class GeneratorCliOptions
{
    private const string DefaultRootNamespace = "CodeAlta.Acp";
    private const string DefaultGithubRepo = "agentclientprotocol/agent-client-protocol";

    public GeneratorCliOptions(
        string? schemaFile,
        string? acpRepoDir,
        string? zipFile,
        string? githubRef,
        AcpSurface surface,
        string rootNamespace,
        string githubRepo,
        string? outputDir,
        string? cacheDir,
        bool forceDownload,
        string? sourceRef = null)
    {
        SchemaFile = schemaFile;
        AcpRepoDir = acpRepoDir;
        ZipFile = zipFile;
        GithubRef = githubRef;
        Surface = surface;
        RootNamespace = rootNamespace;
        GithubRepo = githubRepo;
        OutputDir = outputDir;
        CacheDir = cacheDir;
        ForceDownload = forceDownload;
        SourceRef = sourceRef;
    }

    public string? SchemaFile { get; }

    public string? AcpRepoDir { get; }

    public string? ZipFile { get; }

    public string? GithubRef { get; }

    public AcpSurface Surface { get; }

    public string RootNamespace { get; }

    public string GithubRepo { get; }

    public string? OutputDir { get; }

    public string? CacheDir { get; }

    public bool ForceDownload { get; }

    /// <summary>The tag or commit of a local source, recorded in the generated metadata.</summary>
    public string? SourceRef { get; }

    public static CommandApp CreateCommandApp(Func<GeneratorCliOptions, ValueTask<int>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);

        return CreateCommandAppCore(
            new ParseState(),
            execute);
    }

    private static CommandApp CreateCommandAppCore(
        ParseState state,
        Func<GeneratorCliOptions, ValueTask<int>> execute)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(execute);

        const string _ = "";

        return new CommandApp(
            "CodeAlta.Acp.Generator",
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
            new CommandUsage(),
            _,
            "Inputs:",
            { "schema-file=", "ACP schema {FILE} to use", value => state.SchemaFile = value },
            { "acp-repo-dir=", "Local ACP checkout {DIR}", value => state.AcpRepoDir = value },
            { "zip-file=", "Local ACP archive {FILE}", value => state.ZipFile = value },
            { "github-ref=", "Pinned GitHub tag or branch {REF}", value => state.GithubRef = value },
            { "source-ref=", "Tag or commit {REF} of a local source, recorded in the metadata", value => state.SourceRef = value },
            _,
            "Generation:",
            { "surface=", "ACP {SURFACE}: stable or unstable", value => state.Surface = value },
            { "namespace=", "Root {NAMESPACE} for generated types", value => state.RootNamespace = value },
            { "github-repo=", "GitHub {OWNER/REPO} to download from", value => state.GithubRepo = value },
            { "output-dir=", "Generated output {DIR}", value => state.OutputDir = value },
            { "cache-dir=", "Download cache {DIR}", value => state.CacheDir = value },
            { "force-download", "Ignore cached GitHub archives", value => state.ForceDownload = value is not null },
            new HelpOption(),
            (_, _) =>
            {
                if (!TryCreateOptions(state, out var options, out var error))
                {
                    throw new CommandException(error!);
                }

                return execute(options!);
            },
        };
    }

    private static bool TryCreateOptions(
        ParseState state,
        [NotNullWhen(true)] out GeneratorCliOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(state);

        var specifiedInputs = 0;
        specifiedInputs += string.IsNullOrWhiteSpace(state.SchemaFile) ? 0 : 1;
        specifiedInputs += string.IsNullOrWhiteSpace(state.AcpRepoDir) ? 0 : 1;
        specifiedInputs += string.IsNullOrWhiteSpace(state.ZipFile) ? 0 : 1;
        specifiedInputs += string.IsNullOrWhiteSpace(state.GithubRef) ? 0 : 1;
        if (specifiedInputs != 1)
        {
            options = null;
            error = "Specify exactly one ACP source: --schema-file, --acp-repo-dir, --zip-file, or --github-ref.";
            return false;
        }

        if (!TryParseSurface(state.Surface, out var surface, out error))
        {
            options = null;
            return false;
        }

        options = new GeneratorCliOptions(
            state.SchemaFile,
            state.AcpRepoDir,
            state.ZipFile,
            state.GithubRef,
            surface,
            string.IsNullOrWhiteSpace(state.RootNamespace) ? DefaultRootNamespace : state.RootNamespace.Trim(),
            string.IsNullOrWhiteSpace(state.GithubRepo) ? DefaultGithubRepo : state.GithubRepo.Trim(),
            string.IsNullOrWhiteSpace(state.OutputDir) ? null : state.OutputDir.Trim(),
            string.IsNullOrWhiteSpace(state.CacheDir) ? null : state.CacheDir.Trim(),
            state.ForceDownload,
            string.IsNullOrWhiteSpace(state.SourceRef) ? null : state.SourceRef.Trim());
        error = null;
        return true;
    }

    private static bool TryParseSurface(
        string? value,
        out AcpSurface surface,
        [NotNullWhen(false)] out string? error)
    {
        var normalized = value?.Trim();
        switch (normalized?.ToLowerInvariant())
        {
            case "stable":
                surface = AcpSurface.Stable;
                error = null;
                return true;
            case "unstable":
            case "draft":
                surface = AcpSurface.Unstable;
                error = null;
                return true;
            default:
                surface = default;
                error = "Missing or invalid --surface value. Expected: stable | unstable.";
                return false;
        }
    }

    private sealed class ParseState
    {
        public string? SchemaFile { get; set; }

        public string? AcpRepoDir { get; set; }

        public string? ZipFile { get; set; }

        public string? GithubRef { get; set; }

        public string? Surface { get; set; }

        public string? RootNamespace { get; set; }

        public string? GithubRepo { get; set; }

        public string? OutputDir { get; set; }

        public string? CacheDir { get; set; }

        public bool ForceDownload { get; set; }

        public string? SourceRef { get; set; }
    }
}
