using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The editors of the machine a folder can be opened in beside the code editor of CodeAlta, the one the user
/// chose, and the folder of a project or of a session opened in that editor or in the file manager of the system.
/// The page names a project or a session: the host finds its folder, and starts nothing the page describes.
/// </summary>
[NeoRpcService("externalEditors", Version = 1)]
internal sealed class ExternalEditorsService
{
    /// <summary>The longest identifier a request may carry, in UTF-16 units.</summary>
    internal const int MaximumIdUnits = 128;

    private readonly ExternalEditors? _editors;
    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly Func<string, CancellationToken, Task<string?>>? _sessionFolder;
    private readonly Func<string, bool> _openFolder = DesktopFileReveal.OpenFolder;
    private readonly Action<ExternalEditorChoice>? _choose;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ExternalEditorsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="editors">The editors of the machine.</param>
    /// <param name="choose">Keeps the choice of the user.</param>
    /// <param name="projects">The host's project catalog, used to resolve a project id to its folder.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="sessionFolder">Gives the folder a session works in, or null when there is no such session.</param>
    /// <param name="openFolder">Opens a folder in the file manager; the one of the system by default.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ExternalEditorsService(ExternalEditors editors, Action<ExternalEditorChoice> choose, ProjectCatalog projects, string epoch,
        Func<string, CancellationToken, Task<string?>> sessionFolder, Func<string, bool>? openFolder = null)
    {
        ArgumentNullException.ThrowIfNull(editors);
        ArgumentNullException.ThrowIfNull(choose);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        ArgumentNullException.ThrowIfNull(sessionFolder);
        (_editors, _choose, _projects, _epoch, _sessionFolder) = (editors, choose, projects, epoch, sessionFolder);
        if (openFolder is not null) _openFolder = openFolder;
    }

    /// <summary>
    /// The editors found on the machine, the one folders are opened in, and the choice of the user. The editors
    /// are looked for the first time this is asked, or again when <c>Refresh</c> is set.
    /// </summary>
    [NeoRpcMethod("list")]
    public async Task<ExternalEditorsResponse> ListAsync(ExternalEditorsListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_editors is null) return Unavailable;
        return await DescribeAsync(request.Refresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes the editor the user chose: an editor of the list, <c>custom</c> with its program and its arguments,
    /// or none (the first editor found). <c>invalid</c> for anything else, and nothing changes.
    /// </summary>
    [NeoRpcMethod("set")]
    public async Task<ExternalEditorsResponse> SetAsync(ExternalEditorsSetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_editors is null) return Unavailable;
        if (ExternalEditorChoice.Valid(request.Id, request.Program, request.Arguments) is not { } choice)
            return await DescribeAsync(false, cancellationToken).ConfigureAwait(false) with { Status = "invalid" };
        _choose!(choice);
        return await DescribeAsync(false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the folder a session works in (its worktree while that folder is there), or the folder of a project,
    /// in the external editor (<c>editor</c>) or in the file manager of the system (<c>explorer</c>).
    /// </summary>
    [NeoRpcMethod("openFolder")]
    public async Task<ExternalEditorsOpenResponse> OpenFolderAsync(ExternalEditorsOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_editors is null) return new("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch");
        if (request.Target is not ("editor" or "explorer") || !Identifier(request.ProjectId) || !Identifier(request.SessionId)
            || (request.ProjectId is null && request.SessionId is null)) return new("invalid");
        string? folder = null;
        if (request.ProjectId is not null)
        {
            var project = await SettingsProjectScope.ResolveAsync(_projects!, request.ProjectId, allowArchived: false, cancellationToken).ConfigureAwait(false);
            if (project.Status != "ok") return new(project.Status);
            folder = project.Root;
        }

        if (request.SessionId is not null)
        {
            string? session;
            try { session = await _sessionFolder!(request.SessionId, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException) { return new("read_failed"); }
            if (session is null) return new("missing_session");
            // A session can work in a folder of its own, which is not the one of its project.
            if (Path.IsPathFullyQualified(session) && Directory.Exists(session)) folder = session;
        }

        if (folder is null || !Directory.Exists(folder)) return new("no_folder");
        return request.Target == "explorer"
            ? new(_openFolder(folder) ? "ok" : "failed")
            : new(await _editors.OpenAsync(folder, null, null, null, cancellationToken).ConfigureAwait(false));
    }

    private ExternalEditorsResponse Unavailable => new("unavailable", Platform, [], null, null, null, null);

    private static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    private async Task<ExternalEditorsResponse> DescribeAsync(bool refresh, CancellationToken cancellationToken)
    {
        var found = await _editors!.FoundAsync(refresh, cancellationToken).ConfigureAwait(false);
        var current = await _editors.CurrentAsync(cancellationToken).ConfigureAwait(false);
        var choice = _editors.Choice;
        return new("ok", _editors.Platform, [.. found.Select(static editor => new ExternalEditorItem(editor.Id, editor.Name, editor.Program))],
            current is null ? null : new(current.Id, current.Name, current.Program), choice.Id, choice.Program, choice.Arguments);
    }

    private static bool Identifier(string? value) => value is null || value.Length is > 0 and <= MaximumIdUnits && !value.Any(char.IsControl);
}

/// <param name="Refresh">Looks for the editors again, as after one was installed.</param>
internal sealed record ExternalEditorsListRequest(bool Refresh = false);

/// <param name="Id">An editor of the list, <c>custom</c>, or null for the first editor found.</param>
/// <param name="Program">The full path of the program of a custom editor.</param>
/// <param name="Arguments">
/// The arguments of a custom editor, with <c>{folder}</c>, <c>{file}</c>, <c>{line}</c> and <c>{column}</c>;
/// <c>{folder}</c> when blank.
/// </param>
internal sealed record ExternalEditorsSetRequest(string? Id, string? Program = null, string? Arguments = null);

/// <summary>An editor found on the machine.</summary>
/// <param name="Id">Its id, which is also the name of its logo; <c>custom</c> for the editor the user describes.</param>
/// <param name="Name">Its name.</param>
/// <param name="Program">The full path of the program that is started.</param>
internal sealed record ExternalEditorItem(string Id, string Name, string Program);

/// <param name="Status"><c>ok</c>, <c>invalid</c> (the choice was not one: nothing changed) or <c>unavailable</c>.</param>
/// <param name="Platform"><c>windows</c>, <c>macos</c> or <c>linux</c>: what the file manager is called.</param>
/// <param name="Editors">The editors of the list found on the machine, in the order of the list.</param>
/// <param name="Current">The editor folders are opened in; null when none was found or chosen.</param>
/// <param name="Chosen">The editor the user chose; null when the first editor found is used.</param>
/// <param name="CustomProgram">The program of the custom editor, when it is the one chosen.</param>
/// <param name="CustomArguments">The arguments of the custom editor, when it is the one chosen.</param>
internal sealed record ExternalEditorsResponse(string Status, string Platform, ExternalEditorItem[] Editors, ExternalEditorItem? Current,
    string? Chosen, string? CustomProgram, string? CustomArguments);

/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project whose folder is opened.</param>
/// <param name="SessionId">The session whose folder is opened: its worktree while it is there, else the folder of its project.</param>
/// <param name="Target"><c>editor</c> (the external editor) or <c>explorer</c> (the file manager of the system).</param>
internal sealed record ExternalEditorsOpenRequest(string? ExpectedEpoch, string? ProjectId, string? SessionId, string? Target);

/// <param name="Status">
/// <c>ok</c>, <c>no_editor</c> (none found or chosen), <c>refused</c> (the custom program cannot be started), <c>failed</c>,
/// <c>no_folder</c>, <c>missing_session</c>, a refusal of the project, <c>invalid</c>, <c>stale_epoch</c> or <c>unavailable</c>.
/// </param>
internal sealed record ExternalEditorsOpenResponse(string Status);
