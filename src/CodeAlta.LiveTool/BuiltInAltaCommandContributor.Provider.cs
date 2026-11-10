using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

internal sealed partial class BuiltInAltaCommandContributor
{
    private const string SetProviderCommandPath = "alta session set_provider";

    private static Command CreateSessionSetProviderCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        string? provider = null;
        var command = Leaf("set_provider", "Move an idle session to another provider, as the provider chooser of the composer of the window does.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => sessionId = value);
        command.Add("provider=", "Provider key, one of the `providers` that `alta session choices <session-id>` lists.", value => provider = value);
        command.Add(async (_, _) => await HandleSessionSetProviderAsync(context, FirstNonEmpty(sessionId, context.Caller.SourceSessionId), provider).ConfigureAwait(false));
        AddHelpText(
            command,
            "The session keeps its conversation and its draft: the next prompt runs on the new provider, with its",
            "default model, effort and permission mode, which `alta session send` can then choose. Only an idle",
            "session changes provider: not while it works, nor while a prompt waits in its queue.",
            "Example: `alta session set_provider <session-id> --provider codex`.");
        return command;
    }

    private static async ValueTask<int> HandleSessionSetProviderAsync(AltaCommandContext context, string? sessionId, string? provider)
    {
        context.ClaimSessionWork(sessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required outside a session caller.", SetProviderCommandPath);
        }

        provider = NormalizeOptionalText(provider);
        if (provider is null)
        {
            return UsageError(context, "usage.missingProvider", "--provider is required: one of the `providers` of `alta session choices`.", SetProviderCommandPath);
        }

        if (!context.TryGetRequired<OwnedSessionCommandService>(nameof(OwnedSessionCommandService), out var commands)
            || !context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId.Trim()).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var session = infoResult.Info!.Session;
        if (await RefuseSessionOfOtherSpaceAsync(context, session.SessionId, "changes the provider of").ConfigureAwait(false) is { } otherSpace)
        {
            return otherSpace;
        }

        // The session must be idle: the selection is read and checked as the provider chooser of the window reads it.
        var selection = await commands.GetProviderSelectionAsync(session.SessionId).ConfigureAwait(false);
        if (selection is null)
        {
            return Unsupported(context, "session.running", "The session is working, or CodeAlta is closing: its provider changes once it is idle. Nothing was changed.");
        }

        var previous = selection.ProviderKey;
        var target = selection.Providers.FirstOrDefault(candidate => string.Equals(candidate.ProviderId.Value, provider, StringComparison.Ordinal));
        if (target is null)
        {
            var listed = string.Join(", ", selection.Providers.Take(32).Select(static candidate => candidate.ProviderId.Value));
            return NotFound(context, "provider.notFound", selection.Providers.Count == 0
                ? $"The session can use no other provider in its space now: '{provider}' is not one of them."
                : $"--provider is one of the providers the session can use in its space: {listed}.");
        }

        // A session whose user reviews what it does gives no session the mode of a provider that asks less.
        if (!string.Equals(previous, provider, StringComparison.Ordinal)
            && !runtime.MayGivePermissionMode(context.Caller.SourceSessionId, provider, OwnedSessionSelection.ProviderPermissionMode))
        {
            return PermissionDenied(context, "session.permissionModeDenied",
                $"The user reviews what this session does: it cannot move a session to the provider '{provider}', whose permission mode asks less.");
        }

        var status = string.Equals(previous, provider, StringComparison.Ordinal) ? "unchanged" : await commands.SelectProviderAsync(selection, provider).ConfigureAwait(false);
        int? refused = status switch
        {
            "ok" or "unchanged" or "selected_cleanup_required" => null,
            SessionViewChangeRefusedException.QueuePending => Unsupported(context, "session.queuePending",
                "A prompt waits in the queue of the session: send it or remove it, then change the provider."),
            SessionViewChangeRefusedException.TurnUnfinished => Unsupported(context, "session.turnUnfinished",
                "A turn of the session has not ended: wait until it ends, then change the provider."),
            "interaction_pending" => Unsupported(context, "session.interactionPending",
                "The session waits for an answer (a question or an approval): answer it, then change the provider."),
            SessionRuntimeService.ProviderOutsideSpace => PermissionDenied(context, "provider.notInSpace",
                $"The provider '{provider}' is not available in the space of the session."),
            SessionRuntimeService.InCloudCode => Unsupported(context, "session.inCloud",
                "The session works in the cloud: it keeps its account of Claude Code until it is back."),
            "provider_unavailable" => NotFound(context, "provider.notFound", $"The provider '{provider}' is not enabled."),
            "closed" => Unsupported(context, "host.closing", "CodeAlta is closing: nothing was changed."),
            "selection_unconfirmed" => Unsupported(context, "session.selectionUnconfirmed",
                "The provider of the session may not have changed: `alta session choices` says which one it has now."),
            _ => Unsupported(context, "session.busy", $"The session changed meanwhile, or is in use ({status}): nothing was changed. Try again."),
        };
        if (refused is { } exitCode)
        {
            return exitCode;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.provider_set",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            providerKey = provider,
            previousProviderKey = previous,
            changed = status != "unchanged",
        });
        return AltaExitCodes.Success;
    }
}
