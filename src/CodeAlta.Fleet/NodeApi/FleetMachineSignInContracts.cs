namespace CodeAlta.Fleet.NodeApi;

/// <summary>What a sign-in of a machine signs in (<c>control.signins.*</c>).</summary>
public static class FleetMachineSignInKinds
{
    /// <summary>A model provider of the machine that signs in with an account (Codex, Copilot, xAI).</summary>
    public const string Provider = "provider";

    /// <summary>An HTTP MCP server of the machine that authorizes with OAuth.</summary>
    public const string Mcp = "mcp";

    /// <summary>A command line tool of the machine with its own login (<c>gh</c>, <c>az</c>, <c>claude</c>), in the profile of a space.</summary>
    public const string Cli = "cli";
}

/// <summary>How the person completes a sign-in of a machine.</summary>
public static class FleetMachineSignInWays
{
    /// <summary>The desktop opens a page in its browser, catches its redirect on its loopback and forwards the callback to the machine.</summary>
    public const string Browser = "browser";

    /// <summary>The desktop shows a code that the person enters on a page; the machine waits for the provider to say it was.</summary>
    public const string Device = "device";

    /// <summary>The desktop opens a page that shows a code, and sends back the code the person pastes.</summary>
    public const string Code = "code";

    /// <summary>
    /// The desktop opens a page in its browser and nothing comes back to it: the machine asks the service until it says the person
    /// signed in there (Mistral Vibe).
    /// </summary>
    public const string Page = "page";

    /// <summary>
    /// The desktop asks for a value the person types (an API key), sent once to the machine, which keeps it as a secret of its own
    /// and never gives it back (an ACP agent that reads it from its environment).
    /// </summary>
    public const string Secret = "secret";
}

/// <summary>Where a sign-in of a machine stands, or what it is.</summary>
public static class FleetMachineSignInStates
{
    /// <summary>Signed in.</summary>
    public const string SignedIn = "signed_in";

    /// <summary>Not signed in.</summary>
    public const string SignedOut = "signed_out";

    /// <summary>The machine cannot tell.</summary>
    public const string Unknown = "unknown";

    /// <summary>An attempt waits for the person.</summary>
    public const string Waiting = "waiting";

    /// <summary>An attempt ended signed in.</summary>
    public const string Completed = "completed";

    /// <summary>An attempt ended without a sign-in.</summary>
    public const string Failed = "failed";

    /// <summary>No attempt has this id (it ended long ago, or never was).</summary>
    public const string None = "none";
}

/// <summary>Something a machine signs in: a provider, an MCP server, or a command line tool in the profile of a space.</summary>
/// <param name="Kind">A kind of <see cref="FleetMachineSignInKinds"/>.</param>
/// <param name="Key">The key of the provider or the MCP server, or the name of the tool.</param>
/// <param name="Space">The space whose profile a tool signs in to; null for the machine's own.</param>
public sealed record FleetMachineSignInTarget(string Kind, string Key, string? Space = null);

/// <summary>Something a machine signs in, and how it stands (<c>control.signins.list</c>). Never a token.</summary>
/// <param name="Kind">A kind of <see cref="FleetMachineSignInKinds"/>.</param>
/// <param name="Key">Its key.</param>
/// <param name="Space">The space of its profile; null for the machine's own.</param>
/// <param name="Title">Its name for a person.</param>
/// <param name="State"><see cref="FleetMachineSignInStates.SignedIn"/>, <see cref="FleetMachineSignInStates.SignedOut"/> or <see cref="FleetMachineSignInStates.Unknown"/>.</param>
/// <param name="Account">The account it is signed in as, when the machine knows it.</param>
/// <param name="ExpiresAt">When its sign-in expires, when the machine knows it.</param>
/// <param name="Ways">The ways of <see cref="FleetMachineSignInWays"/> the desktop can sign it in; empty when it is signed in on the machine only.</param>
/// <param name="Detail">What the machine says more of it (why it cannot be signed in from the desktop); null for nothing.</param>
public sealed record FleetMachineSignInItem(string Kind, string Key, string? Space, string Title, string State, string? Account, DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Ways, string? Detail);

/// <summary>What a machine signs in (<c>control.signins.list</c>).</summary>
/// <param name="Items">Its providers, MCP servers and tools, in that order.</param>
public sealed record FleetMachineSignInList(IReadOnlyList<FleetMachineSignInItem> Items);

/// <summary>A sign-in to start on a machine (<c>control.signins.start</c>).</summary>
/// <param name="Kind">A kind of <see cref="FleetMachineSignInKinds"/>.</param>
/// <param name="Key">The key of what to sign in.</param>
/// <param name="Space">The space of its profile; null for the machine's own.</param>
/// <param name="Way">A way of <see cref="FleetMachineSignInWays"/>; null for the first the target offers.</param>
public sealed record FleetMachineSignInStartParams(string Kind, string Key, string? Space = null, string? Way = null);

/// <summary>What the desktop does for a sign-in that started on a machine.</summary>
/// <param name="AttemptId">The attempt, for the calls that follow.</param>
/// <param name="Way">A way of <see cref="FleetMachineSignInWays"/>.</param>
/// <param name="Page">The page to open in the browser of the desktop (for <see cref="FleetMachineSignInWays.Page"/>, the whole sign-in).</param>
/// <param name="Redirect">For <see cref="FleetMachineSignInWays.Browser"/>: the loopback address of the desktop the browser comes back to.</param>
/// <param name="UserCode">For <see cref="FleetMachineSignInWays.Device"/>: the code to enter on the page.</param>
/// <param name="Ask">For <see cref="FleetMachineSignInWays.Code"/> and <see cref="FleetMachineSignInWays.Secret"/>: what the person pastes or types, in words.</param>
/// <param name="ExpiresAt">When the attempt ends if nothing comes.</param>
public sealed record FleetMachineSignInPrompt(string AttemptId, string Way, string? Page, string? Redirect, string? UserCode, string? Ask, DateTimeOffset ExpiresAt);

/// <summary>What the desktop gives back to a sign-in of a machine (<c>control.signins.complete</c>).</summary>
/// <param name="AttemptId">The attempt.</param>
/// <param name="Callback">For <see cref="FleetMachineSignInWays.Browser"/>: the address the browser came back to, with its query.</param>
/// <param name="Code">For <see cref="FleetMachineSignInWays.Code"/>: the code the person pasted; for <see cref="FleetMachineSignInWays.Secret"/>, the value typed.</param>
public sealed record FleetMachineSignInCompleteParams(string AttemptId, string? Callback = null, string? Code = null);

/// <summary>A wait for a sign-in of a machine to end (<c>control.signins.wait</c>).</summary>
/// <param name="AttemptId">The attempt.</param>
/// <param name="WaitSeconds">How long the machine waits before it answers <see cref="FleetMachineSignInStates.Waiting"/>: 0 to 25.</param>
public sealed record FleetMachineSignInWaitParams(string AttemptId, int WaitSeconds = 20);

/// <summary>An attempt to sign a machine in, by its id.</summary>
/// <param name="AttemptId">The attempt.</param>
public sealed record FleetMachineSignInAttemptRef(string AttemptId);

/// <summary>Where an attempt to sign a machine in stands.</summary>
/// <param name="AttemptId">The attempt.</param>
/// <param name="Status"><see cref="FleetMachineSignInStates.Waiting"/>, <see cref="FleetMachineSignInStates.Completed"/>, <see cref="FleetMachineSignInStates.Failed"/> or <see cref="FleetMachineSignInStates.None"/>.</param>
/// <param name="Account">The account signed in, once it is.</param>
/// <param name="Code">Why it failed: <c>timeout</c>, <c>canceled</c>, <c>login_failed</c>.</param>
/// <param name="Message">What the machine says of a failure, in words; never a token.</param>
public sealed record FleetMachineSignInState(string AttemptId, string Status, string? Account, string? Code, string? Message);

/// <summary>
/// The sign-ins a node keeps for itself, made from the desktop that manages it (<c>doc/fleet/node.md</c>, credentials on a node):
/// the node starts each sign-in and keeps its secrets (the code verifier, the tokens); the desktop shows a code, or opens a page in
/// its browser and forwards the callback. The node application implements it; a host without it leaves
/// <see cref="FleetNodeServiceOptions.SignIns"/> null.
/// </summary>
public interface IFleetNodeSignIns
{
    /// <summary>Lists what the machine signs in and how each stands.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The list.</returns>
    Task<FleetMachineSignInList> ListAsync(CancellationToken cancellationToken);

    /// <summary>Starts a sign-in on the machine, and says what the desktop does.</summary>
    /// <param name="parameters">What to sign in, and how.</param>
    /// <param name="cancellationToken">Cancels the wait for the first prompt; the sign-in goes on.</param>
    /// <returns>What the desktop does.</returns>
    /// <exception cref="KeyNotFoundException">The machine has no such target.</exception>
    /// <exception cref="NotSupportedException">The target does not sign in that way from another computer.</exception>
    /// <exception cref="InvalidOperationException">The sign-in could not start.</exception>
    Task<FleetMachineSignInPrompt> StartAsync(FleetMachineSignInStartParams parameters, CancellationToken cancellationToken);

    /// <summary>Gives a sign-in what the desktop caught (the callback of the browser) or what the person pasted, and waits a while for its end.</summary>
    /// <param name="parameters">The attempt and what it is given.</param>
    /// <param name="cancellationToken">Cancels the wait; the sign-in goes on.</param>
    /// <returns>Where it stands.</returns>
    /// <exception cref="ArgumentException">What is given does not fit the attempt.</exception>
    Task<FleetMachineSignInState> CompleteAsync(FleetMachineSignInCompleteParams parameters, CancellationToken cancellationToken);

    /// <summary>Waits a while for a sign-in to end.</summary>
    /// <param name="attemptId">The attempt.</param>
    /// <param name="wait">How long to wait before answering that it waits.</param>
    /// <param name="cancellationToken">Cancels the wait; the sign-in goes on.</param>
    /// <returns>Where it stands.</returns>
    Task<FleetMachineSignInState> WaitAsync(string attemptId, TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>Ends a sign-in that waits.</summary>
    /// <param name="attemptId">The attempt.</param>
    /// <returns>Where it stands.</returns>
    Task<FleetMachineSignInState> CancelAsync(string attemptId);

    /// <summary>Signs a target out on the machine: its tokens are deleted there.</summary>
    /// <param name="target">What to sign out.</param>
    /// <param name="cancellationToken">Cancels the sign-out.</param>
    /// <returns>The target as it stands now.</returns>
    /// <exception cref="KeyNotFoundException">The machine has no such target.</exception>
    /// <exception cref="NotSupportedException">The target is not signed out from another computer.</exception>
    Task<FleetMachineSignInItem> SignOutAsync(FleetMachineSignInTarget target, CancellationToken cancellationToken);
}
