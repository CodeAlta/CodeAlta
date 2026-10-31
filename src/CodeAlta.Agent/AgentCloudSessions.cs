using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent;

/// <summary>
/// What the listings of sessions show of a session that runs in the cloud (a cloud session of Claude Code), or ran
/// there before it was brought back to this computer.
/// </summary>
/// <param name="Id">The identifier of the cloud session (<c>session_…</c>).</param>
/// <param name="Url">Its address on claude.ai, or null.</param>
/// <param name="Title">Its title, as Claude Code gave it, or null.</param>
/// <param name="Continued">Whether it was brought back: the session goes on on this computer.</param>
/// <param name="Continuing">Whether it is being brought back now.</param>
public sealed record AgentSessionCloud(string Id, string? Url, string? Title, bool Continued, bool Continuing);

/// <summary>A program run in a pseudo-terminal of the host (<see cref="IAgentInteractiveTerminal"/>).</summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">Its arguments, one per element.</param>
/// <param name="WorkingDirectory">The folder it runs in.</param>
/// <param name="Environment">Variables set for it over the environment of a terminal; a <see langword="null"/> value removes one.</param>
/// <param name="Title">The title of its terminal, shown when the user is asked to look at it.</param>
public sealed record AgentInteractiveProgram(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    string Title);

/// <summary>How a program run in a pseudo-terminal ended.</summary>
/// <param name="Rows">The text of its screen when it ended, one row per element.</param>
/// <param name="ExitCode">Its exit code; null when it did not exit by itself (it was ended).</param>
/// <param name="Canceled">Whether it was ended because the run was canceled.</param>
/// <param name="TerminalId">The terminal it ran in when the host keeps it to be looked at; null when it is gone.</param>
/// <param name="Shown">Whether its terminal was shown to the user.</param>
public sealed record AgentInteractiveOutcome(IReadOnlyList<string> Rows, int? ExitCode, bool Canceled, string? TerminalId, bool Shown);

/// <summary>
/// A pseudo-terminal of the host, for a program that refuses to run without one (<c>claude --cloud</c>). The program runs
/// hidden; when it waits for the user (a question), its terminal is shown, and the user answers there.
/// </summary>
public interface IAgentInteractiveTerminal
{
    /// <summary>Runs a program until it ends.</summary>
    /// <param name="program">The program.</param>
    /// <param name="asks">Tells from the rows of its screen whether the program waits for the user.</param>
    /// <param name="onShown">Called once when its terminal is shown to the user, or null.</param>
    /// <param name="cancellationToken">Ends the program.</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="asks"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The program could not be started.</exception>
    Task<AgentInteractiveOutcome> RunAsync(AgentInteractiveProgram program, Func<IReadOnlyList<string>, bool> asks, Action? onShown,
        CancellationToken cancellationToken);
}

/// <summary>What starts a cloud session.</summary>
/// <param name="WorkingDirectory">The folder of the project it starts from.</param>
/// <param name="Task">What the cloud session is to do: its first prompt.</param>
/// <param name="ProjectName">The name of the project, for the title of the terminal.</param>
internal sealed record AgentCloudStartRequest(string WorkingDirectory, string Task, string ProjectName);

/// <summary>How a start of a cloud session ended.</summary>
/// <param name="Status">
/// <c>created</c>; <c>failed</c> (<paramref name="Message"/> says why, in the words of the provider); <c>canceled</c>;
/// <c>terminal_unavailable</c> when the host has no terminal; <c>unavailable</c> when the provider cannot run.
/// </param>
/// <param name="SessionId">The cloud session, when created.</param>
/// <param name="Url">Its address, when created.</param>
/// <param name="Title">Its title, when the provider gave one.</param>
/// <param name="Note">What the provider said of where it started (a branch that is not on GitHub), or null.</param>
/// <param name="Message">Why it failed, or null.</param>
/// <param name="TerminalId">The terminal kept to be looked at after a failure, or null.</param>
internal sealed record AgentCloudStart(string Status, string? SessionId = null, string? Url = null, string? Title = null, string? Note = null,
    string? Message = null, string? TerminalId = null);

/// <summary>How a message sent to a cloud session ended.</summary>
/// <param name="Status">
/// <c>ok</c>, <c>archived</c>, <c>not_found</c>, <c>needs_claude_ai</c> (the account cannot use cloud sessions),
/// <c>policy</c> (the organization does not allow them), <c>failed</c> or <c>unavailable</c>.
/// </param>
/// <param name="Message">What the provider said, or null.</param>
internal sealed record AgentCloudSendResult(string Status, string? Message = null);

/// <summary>How a cloud session was brought back to this computer.</summary>
/// <param name="Status"><c>ok</c>, <c>failed</c> or <c>unavailable</c>.</param>
/// <param name="Message">Why it failed, in the words of the provider when it said something, or null.</param>
internal sealed record AgentCloudReturn(string Status, string? Message = null);

/// <summary>
/// A turn executor whose provider can run a session in the cloud (Claude Code's cloud sessions): start it there, send it
/// messages, and bring its conversation back to this computer, into a conversation of the provider here.
/// </summary>
internal interface IAgentProviderCloudSessions
{
    /// <summary>Starts a cloud session in a terminal of the host: the provider refuses to start one without a terminal.</summary>
    /// <param name="request">What starts it.</param>
    /// <param name="terminal">The terminal of the host, or null when it has none.</param>
    /// <param name="onShown">Called when the terminal is shown to the user, who is asked something there; or null.</param>
    /// <param name="cancellationToken">Ends the start.</param>
    Task<AgentCloudStart> StartCloudSessionAsync(AgentCloudStartRequest request, IAgentInteractiveTerminal? terminal, Action? onShown,
        CancellationToken cancellationToken);

    /// <summary>Sends a message to a cloud session.</summary>
    /// <param name="cloudSessionId">The cloud session.</param>
    /// <param name="message">The message.</param>
    /// <param name="workingDirectory">The folder the provider runs in.</param>
    /// <param name="cancellationToken">Ends the send.</param>
    Task<AgentCloudSendResult> SendToCloudSessionAsync(string cloudSessionId, string message, string workingDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Brings the conversation of a cloud session to this computer, as a new conversation of the provider in a folder,
    /// without a model call. The folder must have no uncommitted change to a tracked file.
    /// </summary>
    /// <param name="cloudSessionId">The cloud session.</param>
    /// <param name="conversationId">The identifier of the conversation it is brought into.</param>
    /// <param name="workingDirectory">The folder: the provider checks out the branch of the cloud session there.</param>
    /// <param name="cancellationToken">Ends the bring-back.</param>
    Task<AgentCloudReturn> BringCloudSessionBackAsync(string cloudSessionId, string conversationId, string workingDirectory, CancellationToken cancellationToken);

    /// <summary>Reads the turns a bring-back wrote, made in the cloud, to add them to the session.</summary>
    /// <param name="conversationId">The conversation it was brought into.</param>
    /// <param name="workingDirectory">The folder it was brought into.</param>
    /// <param name="users">The user messages the session holds already.</param>
    /// <param name="assistants">The assistant messages the session holds already.</param>
    /// <param name="fromTerminal">Whether it was brought back in a terminal, where turns may follow.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    AgentExternalTurns ReadBroughtBackTurns(string conversationId, string workingDirectory, int users, int assistants, bool fromTerminal,
        CancellationToken cancellationToken);

    /// <summary>
    /// The command that brings a cloud session back in a terminal, into the conversation given (it starts with
    /// <c>claude</c>), the account folder it is run with (null when the provider leaves it to the environment), and the
    /// executable the provider runs, to type in the place of <c>claude</c> (null when it is not known).
    /// </summary>
    /// <param name="cloudSessionId">The cloud session.</param>
    /// <param name="conversationId">The conversation it is brought into.</param>
    (string Command, string? ConfigDirectory, string? Executable) BringBackCommand(string cloudSessionId, string conversationId);

    /// <summary>Reads the cloud session a text a user pasted names: its identifier, or its link.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The identifier and the address of the cloud session, or null when the text names none.</returns>
    (string Id, string Url)? FindCloudSession(string text);
}
