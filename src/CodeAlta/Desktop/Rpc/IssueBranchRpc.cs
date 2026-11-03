using CodeAlta.Catalog.Issues;
using NeoAstra.Rpc;
using Tomlyn;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The form of the scheme of the branches of a project (the <c>[branch]</c> table of <c>.alta/issues.toml</c> and
/// <c>.alta/local/issues.toml</c>): what it shows for one file, and writing or removing the table of that file.
/// </summary>
internal sealed partial class IssuesService
{
    /// <summary>Reads the scheme the form shows for the team's file or the user's.</summary>
    [NeoRpcMethod("branchScheme")]
    public async Task<IssueBranchSchemeFormResponse> BranchSchemeAsync(IssueBranchSchemeFormRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (Source(request.Scope) is not { } source) return new("invalid_request");
        if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found");
        var form = IssueBranchSchemes.ReadForm(source, project.ProjectPath);
        return new("ok", Branch(new(form.Scheme, form.Problem)), form.Own, IssueViewStore.Locate(source, project.ProjectPath));
    }

    /// <summary>Writes the whole <c>[branch]</c> table of the team's file or the user's, or removes it, and answers the scheme that applies then.</summary>
    [NeoRpcMethod("saveBranchScheme")]
    public async Task<IssueBranchSchemeSaveResponse> SaveBranchSchemeAsync(IssueBranchSchemeSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (Source(request.Scope) is not { } source) return new("invalid_request");
        if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found");
        IssueBranchScheme? scheme = null;
        if (!request.Remove)
        {
            if (request.Template is null || request.TitleLength is not { } length || request.Types is not { } types
                || types.Any(static type => type?.Name is null || type.Value is null)) return new("invalid_request");
            scheme = new(request.Template.Trim(), length, [.. types.Select(static type => new IssueBranchType(type.Name.Trim(), type.Value.Trim()))]);
            if (IssueBranchSchemes.SchemeProblem(scheme) is { } problem) return new("invalid", problem);
        }

        try
        {
            // The views are written under the same gate: both edit the same files.
            lock (_viewsGate) IssueBranchSchemes.Save(source, project.ProjectPath, scheme);
        }
        catch (Exception exception) when (exception is TomlException or InvalidDataException or InvalidOperationException or FormatException)
        {
            // What names the file says why (a scheme written in a form the window does not change); the rest is a file that does not parse.
            var path = IssueViewStore.Locate(source, project.ProjectPath);
            return new("config_invalid", exception is InvalidDataException && exception.Message.Contains(path, StringComparison.Ordinal)
                ? exception.Message : $"'{path}' could not be read: fix it, then save again.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("failed", exception.Message.Split('\n', 2)[0].Trim());
        }

        return new("ok", null, Branch(IssueBranchSchemes.Read(project.ProjectPath)));
    }
}

/// <summary>Asks for the scheme the form shows for one file.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="Scope"><c>team</c> for <c>.alta/issues.toml</c>, <c>personal</c> for <c>.alta/local/issues.toml</c>.</param>
internal sealed record IssueBranchSchemeFormRequest(string ExpectedEpoch, string ProjectId, string Scope);

/// <summary>
/// The scheme the form shows. Status is <c>ok</c>, <c>invalid_request</c>, <c>not_found</c>, <c>unavailable</c> or <c>stale_epoch</c>.
/// </summary>
/// <param name="Status">How the reading ended.</param>
/// <param name="Scheme">The scheme of the file, else the one that applies without it, with what is wrong in the files read.</param>
/// <param name="Own">Whether the file has a <c>[branch]</c> table.</param>
/// <param name="Path">The path of the file.</param>
internal sealed record IssueBranchSchemeFormResponse(string Status, IssueBranchSchemeRow? Scheme = null, bool Own = false, string? Path = null);

/// <summary>Asks to write the <c>[branch]</c> table of one file, or to remove it.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="Scope"><c>team</c> or <c>personal</c>.</param>
/// <param name="Remove">Removes the table: the scheme of the files below applies again.</param>
/// <param name="Template">The template; needed unless the table is removed.</param>
/// <param name="TitleLength">The longest <c>{title}</c>; needed unless the table is removed.</param>
/// <param name="Types">What <c>{type}</c> is for each type, in order, <c>*</c> for the others; needed unless the table is removed.</param>
internal sealed record IssueBranchSchemeSaveRequest(string ExpectedEpoch, string ProjectId, string Scope, bool Remove, string? Template = null, int? TitleLength = null,
    IReadOnlyList<IssueBranchTypeRow>? Types = null);

/// <summary>
/// What writing the scheme gave. Status is <c>ok</c> (with the scheme that applies now), <c>invalid</c> (with what is wrong),
/// <c>config_invalid</c> (the file does not parse and is left as it is), <c>failed</c>, <c>invalid_request</c>,
/// <c>not_found</c>, <c>unavailable</c> or <c>stale_epoch</c>.
/// </summary>
/// <param name="Status">How the writing ended.</param>
/// <param name="Message">What is wrong, for <c>invalid</c>, <c>config_invalid</c> and <c>failed</c>.</param>
/// <param name="Branch">The scheme that applies now, for <c>ok</c>.</param>
internal sealed record IssueBranchSchemeSaveResponse(string Status, string? Message = null, IssueBranchSchemeRow? Branch = null);
