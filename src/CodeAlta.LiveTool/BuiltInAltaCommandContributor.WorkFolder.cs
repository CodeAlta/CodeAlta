using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

internal sealed partial class BuiltInAltaCommandContributor
{
    private const string SetWorktreeCommandPath = "alta session set_worktree";

    private static Command CreateSessionSetWorktreeCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        string? path = null;
        string? worktreeBase = null;
        var create = false;
        var projectFolder = false;
        var command = Leaf("set_worktree", "Move a project session to another git worktree of its project, a new one, or back to the project folder.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => sessionId = value);
        command.Add("path=", "A folder of an existing git worktree of the project's repository, wherever it is.", value => path = value);
        command.Add("new", "Create a git worktree for the session: a checkout of its own, on a new branch `alta/<name>`.", value => create = value is not null);
        command.Add("base=", "Branch or commit a new worktree starts from. Defaults to the commit the project folder is on.", value => worktreeBase = value);
        command.Add("project-folder", "Work in the folder of the project again.", value => projectFolder = value is not null);
        command.Add(async (_, _) => await HandleSessionSetWorktreeAsync(context, FirstNonEmpty(sessionId, context.Caller.SourceSessionId), path, create,
            worktreeBase, projectFolder).ConfigureAwait(false));
        AddHelpText(
            command,
            "The session keeps its conversation and its identity; its file tools and commands run in the new folder,",
            "and its next prompt tells its agent where it works now. No file is moved, copied or removed: changes",
            "that are not committed stay in the folder the session leaves. Do that part yourself first when the",
            "user wants them along (commit them on the branch, or stash and apply them in the other folder).",
            "`--path` takes any git worktree of the same repository, also one outside the worktrees folder of CodeAlta",
            "(made with `git worktree add`); a separate clone is another repository and is refused.",
            "Another session changes folder while it is idle. The calling session changes folder when its turn ends:",
            "the answer is then `scheduled`, and what waits in its queue runs in the new folder.",
            "Examples:",
            "  `alta session set_worktree --path C:\\code\\MyProject-feature`",
            "  `alta session set_worktree <session-id> --new --base main`",
            "  `alta session set_worktree <session-id> --project-folder`");
        return command;
    }

    private static async ValueTask<int> HandleSessionSetWorktreeAsync(AltaCommandContext context, string? sessionId, string? path, bool create,
        string? worktreeBase, bool projectFolder)
    {
        context.ClaimSessionWork(sessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required outside a session caller.", SetWorktreeCommandPath);
        }

        path = NormalizeOptionalText(path);
        if ((path is not null ? 1 : 0) + (create ? 1 : 0) + (projectFolder ? 1 : 0) != 1)
        {
            return UsageError(context, "usage.missingTarget", "Name where the session works: one of --path <folder>, --new or --project-folder.", SetWorktreeCommandPath);
        }

        worktreeBase = NormalizeOptionalText(worktreeBase);
        if (worktreeBase is not null && (!create || !GitWorktreeService.IsReferenceName(worktreeBase)))
        {
            return UsageError(context, "usage.invalidBase", "--base names the branch or commit a new worktree starts from: use it with --new.", SetWorktreeCommandPath);
        }

        if (path is not null && !Path.IsPathFullyQualified(path))
        {
            return UsageError(context, "usage.invalidPath", "--path is an absolute folder.", SetWorktreeCommandPath);
        }

        if (!context.TryGetRequired<OwnedSessionCommandService>(nameof(OwnedSessionCommandService), out var commands)
            || !context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId.Trim()).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var session = infoResult.Info!.Session;
        if (await RefuseSessionOfOtherSpaceAsync(context, session.SessionId, "moves to another folder").ConfigureAwait(false) is { } otherSpace)
        {
            return otherSpace;
        }

        if (session.Kind != SessionViewKind.ProjectSession || NormalizeOptionalText(session.ProjectRef) is not { } projectId
            || await catalog.GetByIdAsync(projectId, context.CancellationToken).ConfigureAwait(false) is not { Archived: false } project
            || !string.Equals(project.ProjectPath, session.WorkingDirectory, StringComparison.Ordinal))
        {
            return Unsupported(context, "session.notProjectSession",
                $"Session '{session.SessionId}' is not a session of a project: only a project session works in a git worktree.");
        }

        // A sandboxed session writes in the folders of its project: an existing checkout elsewhere is outside them.
        if (path is not null && await AltaSandboxGate.OfCallerAsync(context).ConfigureAwait(false) is { } sandbox
            && GitWorktreeService.ResolveSessionCheckout(project.ProjectPath, path) is (GitWorktreeService.Ok, not null))
        {
            return PermissionDenied(context, "sandbox.folderDenied", AltaSandboxGate.Refusal(sandbox, $"move a session to the folder '{path}'"));
        }

        GitWorktreeCreation? created = null;
        var folder = path;
        if (create)
        {
            var creation = await CreateSessionWorktreeAsync(context, project, worktreeBase).ConfigureAwait(false);
            if (creation.Worktree is null)
            {
                return creation.ExitCode;
            }

            created = creation.Worktree;
            folder = created.Folder;
        }

        // The calling session runs this command: it changes folder when its turn ends.
        var whenIdle = IsCaller(context, session.SessionId);
        // A wait that is cancelled leaves the change going on: a new worktree stays, since the session may work there.
        var result = await commands.SwitchWorkFolderAsync(session.SessionId, project.Id, project.ProjectPath, folder, whenIdle)
            .WaitAsync(context.CancellationToken).ConfigureAwait(false);
        if (result.Status is not ("ok" or "unchanged" or "scheduled"))
        {
            // A new worktree goes only when the change was refused for sure: an unconfirmed one may have been made.
            if (created is not null && result.Status != "switch_unconfirmed") await RemoveUnusedWorktreeAsync(context, project, created).ConfigureAwait(false);
            var (exitCode, code, message) = DescribeWorkFolderRefusal(result.Status, session.SessionId, path ?? folder ?? project.ProjectPath, project);
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, exitCode, message);
            return exitCode;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.worktree_set",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            status = result.Status,
            projectId = project.Id,
            workingDirectory = project.ProjectPath,
            worktreeDirectory = result.WorktreeDirectory,
            worktreeBranch = created?.Branch,
            worktreeWarning = created?.Warning,
            note = result.Status == "scheduled"
                ? "The session changes folder when its current turn ends: end your turn, and work in the new folder from the next one."
                : null,
            warnings = result.Warnings,
        });
        return AltaExitCodes.Success;
    }

    // Why a session did not change folder, as the error of the command.
    internal static (int ExitCode, string Code, string Message) DescribeWorkFolderRefusal(string status, string sessionId, string folder, ProjectDescriptor project)
        => status switch
        {
            "session_missing" => (AltaExitCodes.NotFound, "session.notFound", $"Session '{sessionId}' was not found in project '{project.Id}'."),
            "busy" or "session_in_use" => (AltaExitCodes.Unsupported, "session.running",
                $"Session '{sessionId}' is at work, or another change of sessions is under way: a session changes folder while it is idle. Wait for its run to end, then run the command again."),
            "background_running" => (AltaExitCodes.Unsupported, "session.backgroundRunning",
                $"Session '{sessionId}' has background commands that run in its current folder: wait for them to end, then run the command again."),
            "interaction_pending" => (AltaExitCodes.Unsupported, "session.interactionPending",
                $"Session '{sessionId}' waits for an answer to a question or to a permission request: answer it, then run the command again."),
            "invalid" => (AltaExitCodes.Usage, "usage.invalidPath", $"'{folder}' is not an absolute folder."),
            "not_found" => (AltaExitCodes.NotFound, "worktree.notFound", $"The folder '{folder}' does not exist."),
            "not_repository" => (AltaExitCodes.Unsupported, "worktree.notRepository",
                $"'{folder}' is not in a git checkout, or the folder of project '{project.Id}' is not in a git repository."),
            "other_repository" => (AltaExitCodes.Unsupported, "worktree.otherRepository",
                $"'{folder}' is not a checkout of the repository of project '{project.Id}': a separate clone is another repository. Make it a worktree of that repository (`git worktree add <folder> <branch>` in '{project.ProjectPath}'), or use --new."),
            "project_missing" => (AltaExitCodes.Unsupported, "worktree.projectMissing",
                $"The checkout of '{folder}' has no folder where project '{project.Id}' is in its own checkout."),
            "closed" => (AltaExitCodes.ServiceUnavailable, "service.closed", "CodeAlta is closing: nothing was done."),
            _ => (AltaExitCodes.Failure, "session.worktreeUnconfirmed",
                $"Session '{sessionId}' may not have changed folder: check it with `alta session info {sessionId}` before trying again."),
        };
}
