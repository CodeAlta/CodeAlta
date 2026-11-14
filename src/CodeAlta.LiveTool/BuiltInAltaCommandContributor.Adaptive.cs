using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta adaptive` commands: a session on Adaptive has the critics of its level review its change, and reads where it stands.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static Command CreateAdaptiveCommand(AltaCommandContext context)
    {
        var group = Group("adaptive", "For a session on Adaptive: have the critics of its level review the code it changed, and read where it stands.");
        group.Add(CreateAdaptiveReviewCommand(context));
        group.Add(CreateAdaptiveStatusCommand(context));
        AddHelpText(
            group,
            "A session on Adaptive runs on the model of its level, and critics of another model review the code it changes: they get the request of the user and the diff, read the code and run tests, read-only.",
            "The session starts that review itself, when its change is complete and before it reports it, pushes it or merges it. CodeAlta starts none by itself: it only reminds a session whose turn ends with code that was not reviewed.",
            "Examples: `alta adaptive review`; `alta adaptive review --base origin/main --message \"Fixes of the two defects found in the pull request\"`; `alta adaptive status`.");
        return group;
    }

    private static Command CreateAdaptiveReviewCommand(AltaCommandContext context)
    {
        string? baseCommit = null;
        string? message = null;
        var useStdin = false;
        var command = Leaf("review", "Have the critics of the calling session review the code it changed, and return at once.");
        command.Add("base=", "The commit your change starts from (a commit, a branch, a tag), when the folder also holds changes that are not yours: a branch you checked out, a pull. Without it: since the last review of the request, else since its start.", value => baseCommit = value);
        command.Add("message=", "What your change is for, in a few lines, when the request of the user does not say it.", value => message = value);
        command.Add("stdin", "Read that text from stdin instead of `--message`.", value => useStdin = value is not null);
        command.Add(async (_, _) => await HandleAdaptiveReviewAsync(context, baseCommit, message, useStdin).ConfigureAwait(false));
        AddHelpText(
            command,
            "Run it when your change is complete and its checks pass. The answer names the files under review: when they are not your change, ask again with `--base <commit>` once the verdict came.",
            "The verdict of the critics comes back to the session as a prompt that starts with `[CodeAlta Adaptive]`: their approval, or what they found. Nothing has to be polled: go on with work that does not touch the files under review, or end your turn, which that prompt starts again.",
            "Until the verdict comes, do not push, merge or publish the change. Then check each finding, fix the real ones, and do what waited for the review; these fixes need no second review.",
            "Examples: `alta adaptive review`; `alta adaptive review --base 1a2b3c4`; `alta adaptive review --stdin`.");
        return command;
    }

    private static Command CreateAdaptiveStatusCommand(AltaCommandContext context)
    {
        var command = Leaf("status", "Show whether the calling session is on Adaptive, its level and its critics, the review that runs, and the code that changed since the last review.");
        command.Add(async (_, _) => await HandleAdaptiveStatusAsync(context).ConfigureAwait(false));
        return command;
    }

    private static async ValueTask<int> HandleAdaptiveReviewAsync(AltaCommandContext context, string? baseCommit, string? message, bool useStdin)
    {
        const string path = "alta adaptive review";
        var sessionId = NormalizeOptionalText(context.Caller.SourceSessionId);
        if (sessionId is null)
        {
            return UsageError(context, "usage.missingSession", "A session asks for the review of its own change: no session calls.", path);
        }

        context.ClaimSessionWork(sessionId);
        if (context.Services.Get<SessionRuntimeService>() is not { } runtime)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
                "Required in-process service 'SessionRuntimeService' is unavailable.");
            return AltaExitCodes.ServiceUnavailable;
        }

        if (message is not null && useStdin)
        {
            return UsageError(context, "usage.messageConflict", "Use either --message or --stdin, not both.", path);
        }

        var notes = NormalizeOptionalText(useStdin ? await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false) : message);
        var receipt = await runtime.RequestAdaptiveReviewAsync(new AdaptiveReviewRequest(sessionId, NormalizeOptionalText(baseCommit), notes), context.CancellationToken).ConfigureAwait(false);
        if (receipt.Status == "invalid_base")
        {
            return UsageError(context, "usage.invalidBase", receipt.Message, path);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.adaptive.review",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["sessionId"] = sessionId,
            ["status"] = receipt.Status,
            ["level"] = receipt.Level,
            ["critics"] = receipt.Critics,
            ["files"] = receipt.Files,
            ["nextStep"] = receipt.Message,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleAdaptiveStatusAsync(AltaCommandContext context)
    {
        var sessionId = NormalizeOptionalText(context.Caller.SourceSessionId);
        if (sessionId is null)
        {
            return UsageError(context, "usage.missingSession", "A session reads where it stands with Adaptive: no session calls.", "alta adaptive status");
        }

        if (context.Services.Get<SessionRuntimeService>() is not { } runtime)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
                "Required in-process service 'SessionRuntimeService' is unavailable.");
            return AltaExitCodes.ServiceUnavailable;
        }

        var status = await runtime.GetAdaptiveStatusAsync(sessionId, context.CancellationToken).ConfigureAwait(false);
        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.adaptive.status",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["sessionId"] = sessionId,
            ["adaptive"] = status.Adaptive,
            ["level"] = status.Level,
            ["critics"] = status.Critics,
            ["reviewRunning"] = status.ReviewStartedAt is not null,
            ["reviewStartedAt"] = status.ReviewStartedAt,
            ["unreviewedFiles"] = status.UnreviewedFiles,
        });
        return AltaExitCodes.Success;
    }
}
