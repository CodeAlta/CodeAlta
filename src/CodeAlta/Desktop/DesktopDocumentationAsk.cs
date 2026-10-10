using CodeAlta.Agent;
using CodeAlta.Catalog.Documentation;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Desktop;

/// <summary>What asking a question about the user guide gave.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>no_provider</c> when no model provider is enabled; <c>busy</c> when the host takes no more command
/// now; <c>closing</c> when the application is closing; <c>not_sent</c> when the chat was created and its prompt was
/// refused; <c>failed</c> for anything else.
/// </param>
/// <param name="SessionId">The chat that was created; null when none was.</param>
/// <param name="Problem">A line that says why, when the host has one; never a path or the text of an exception of the system.</param>
internal sealed record DocumentationAskResult(string Status, string? SessionId = null, string? Problem = null);

/// <summary>The operations behind <see cref="DocumentationAsker"/>; tests supply literal callbacks.</summary>
/// <param name="Providers">Lists the enabled providers, in the order they are listed.</param>
/// <param name="DefaultProvider">Gives the default provider the configuration of the user names; null when it names none.</param>
/// <param name="HasDefaultPrompt">Whether the default agent prompt is there; null when the application is closing.</param>
/// <param name="HasCapacity">Whether the host takes one more command.</param>
/// <param name="CreateChat">Creates a chat, a session of no project, with a provider and a title.</param>
/// <param name="Send">Sends the first prompt of the chat; returns null when it was accepted, else a word for the refusal.</param>
internal sealed record DocumentationAskOperations(
    Func<IReadOnlyList<ModelProviderDescriptor>> Providers,
    Func<string?> DefaultProvider,
    Func<CancellationToken, Task<bool?>> HasDefaultPrompt,
    Func<bool> HasCapacity,
    Func<ModelProviderDescriptor, string, Task<string>> CreateChat,
    Func<OwnedTextSendRequest, string?> Send);

/// <summary>
/// Asks an agent a question about the user guide: a new chat, with the default provider of the configuration and the
/// model that provider starts with, whose first prompt is the question followed by where the guide is.
/// </summary>
/// <remarks>
/// Nothing of the window decides the provider or the model (not the project or the session it shows), and nothing is
/// saved: the chat is created as the window creates one for a user who chose nothing. Everything is checked before
/// the chat is created, so that a refusal leaves no empty chat behind.
/// </remarks>
internal sealed class DocumentationAsker
{
    /// <summary>The longest question that is asked.</summary>
    internal const int MaximumQuestionLength = 2000;

    private const int MaximumTitleLength = 72;

    private readonly ShippedDocumentation _documentation;
    private readonly DocumentationAskOperations _operations;

    /// <summary>Creates the asker of a host.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DocumentationAsker(CodeAltaHost host, ShippedDocumentation documentation)
        : this(documentation, Operations(host ?? throw new ArgumentNullException(nameof(host))))
    {
    }

    /// <summary>Creates the asker over literal operations.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DocumentationAsker(ShippedDocumentation documentation, DocumentationAskOperations operations)
    {
        ArgumentNullException.ThrowIfNull(documentation);
        ArgumentNullException.ThrowIfNull(operations);
        (_documentation, _operations) = (documentation, operations);
    }

    /// <summary>Whether a text is a question that can be asked: some text, on a few lines, without control characters.</summary>
    internal static bool IsQuestion(string? question)
        => question is { Length: > 0 and <= MaximumQuestionLength } && !string.IsNullOrWhiteSpace(question)
            && !question.Any(static character => char.IsControl(character) && character is not ('\n' or '\r' or '\t'));

    /// <summary>Asks a question about the guide, or about one of its pages.</summary>
    /// <param name="question">The question, as the user wrote it.</param>
    /// <param name="page">The page the question is about, as the guide writes its path; null for the whole guide.</param>
    /// <param name="cancellationToken">Cancels the request before the chat is created.</param>
    /// <returns>The chat, or why the question was not asked. Failures are results, not exceptions.</returns>
    /// <exception cref="ArgumentException"><paramref name="question"/> is no question, or <paramref name="page"/> is no page of the guide.</exception>
    /// <exception cref="OperationCanceledException">The request was canceled before the chat was created.</exception>
    internal async Task<DocumentationAskResult> AskAsync(string question, string? page, CancellationToken cancellationToken)
    {
        if (!IsQuestion(question)) throw new ArgumentException("A question is required.", nameof(question));
        var title = page is null ? null : _documentation.ListPages().FirstOrDefault(known => string.Equals(known.Path, page, StringComparison.Ordinal))?.Title;
        if (page is not null && (title is null || _documentation.GetPageFile(page) is null)) throw new ArgumentException("The page is no page of the guide.", nameof(page));
        string? sessionId = null;
        try
        {
            if (DesktopDefaultProvider.Pick(_operations.Providers(), _operations.DefaultProvider()) is not { } provider) return new("no_provider");
            switch (await _operations.HasDefaultPrompt(cancellationToken).ConfigureAwait(false))
            {
                case null: return new("closing");
                case false: return new("failed", Problem: $"There is no agent prompt '{AgentPromptCatalog.DefaultPromptName}'.");
            }

            if (!_operations.HasCapacity()) return new("busy");
            cancellationToken.ThrowIfCancellationRequested();
            sessionId = await _operations.CreateChat(provider, Title(question)).ConfigureAwait(false);
            // No model: the chat starts with the one its provider is configured with, as any new chat does.
            var selection = new OwnedSessionSelection(provider.ProviderId.Value, AgentPromptCatalog.DefaultPromptName, null, null);
            var refusal = _operations.Send(new OwnedTextSendRequest("documentation:" + Guid.NewGuid().ToString("N"), sessionId, Prompt(question, page, title)) { Selection = selection });
            return refusal is null ? new("ok", sessionId) : new("not_sent", sessionId, refusal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException
            or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            // The text of the exception may name a folder of the user: the page is told that it failed, not why.
            return new(exception is ObjectDisposedException ? "closing" : sessionId is null ? "failed" : "not_sent", sessionId);
        }
    }

    /// <summary>The first prompt of the chat: the question, then where the answer is to be found and how to give it.</summary>
    internal string Prompt(string question, string? page, string? pageTitle)
    {
        var about = page is null
            ? "Asked from the Documentation of CodeAlta."
            : $"Asked from the Documentation of CodeAlta, on the page \"{pageTitle}\" (`{page}`).";
        var start = page is null ? string.Empty : $" Start with `{_documentation.GetPageFile(page)}`.";
        // The line ends are written out: the text does not depend on how this file was checked out.
        return question.Trim().ReplaceLineEndings("\n") + "\n\n---\n"
            + $"{about} Answer from the user guide that ships with CodeAlta, in `{_documentation.Root}`.{start} `{ShippedDocumentation.HomePage}` lists the pages; "
            + "`alta documentation search <text>` finds a text in them and `alta documentation read <page>` prints one. Keep the answer short. "
            + "End it with the pages it comes from, each as a Markdown link to its file. If the guide does not answer, say so.";
    }

    // The name of the chat: the first line of the question, shortened.
    private static string Title(string question)
    {
        var line = question.Trim().ReplaceLineEndings("\n").Split('\n')[0].Replace('\t', ' ').Trim();
        if (line.Length <= MaximumTitleLength) return line;
        var end = MaximumTitleLength - 1;
        if (char.IsHighSurrogate(line[end - 1])) end--;
        return line[..end].TrimEnd() + "…";
    }

    private static DocumentationAskOperations Operations(CodeAltaHost host)
    {
        var defaults = new DesktopDefaultProvider(host.CatalogOptions);
        return new(
            () => [.. host.ModelProviderRegistry.ListProviders().Where(static provider => provider.IsEnabled)],
            () => defaults.Configured(),
            async token =>
            {
                var prompts = await host.Commands.GetDraftPromptChoicesAsync(null, token).ConfigureAwait(false);
                return prompts is null ? null : prompts.Any(static prompt => prompt.Id == AgentPromptCatalog.DefaultPromptName);
            },
            () => host.Commands.HasCapacity,
            async (provider, title) => (await host.Commands.CreateDraftSessionAsync(null, provider, title).ConfigureAwait(false)).SessionId,
            request =>
            {
                var admission = host.Commands.AdmitSend(request, CancellationToken.None);
                return admission.Kind is (OwnedSessionCommandAdmissionKind.Accepted or OwnedSessionCommandAdmissionKind.Replay) && admission.Receipt is not null
                    ? null
                    : admission.Kind.ToString().ToLowerInvariant();
            });
    }
}
