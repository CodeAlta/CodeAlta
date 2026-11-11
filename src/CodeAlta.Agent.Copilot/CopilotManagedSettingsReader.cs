using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace CodeAlta.Agent.Copilot;

/// <summary>
/// Asks the Copilot runtime for the enterprise managed settings that apply to GitHub accounts, through the Copilot SDK
/// (<c>managedSettings.resolve</c>), the way GitHub offers it to the applications that embed Copilot. The runtime runs
/// only while it answers, in a home of its own: it never reads or writes the sign-in or the settings of the user's own
/// Copilot CLI, and it is given the GitHub token of a provider of CodeAlta.
/// </summary>
public sealed class CopilotManagedSettingsReader
{
    /// <summary>The name CodeAlta gives itself to the managed-settings service of GitHub.</summary>
    public const string ClientName = "CodeAlta";

    private readonly string _executable;
    private readonly string _home;
    private readonly string? _preview;

    /// <summary>Initializes a new instance of the <see cref="CopilotManagedSettingsReader"/> class.</summary>
    /// <param name="executable">The runtime's entry point (<see cref="CopilotRuntimeInstallation.Executable"/>).</param>
    /// <param name="home">The folder the runtime keeps its state in (<c>COPILOT_HOME</c>), its cache of policies included.</param>
    /// <exception cref="ArgumentException">An argument is blank.</exception>
    public CopilotManagedSettingsReader(string executable, string home)
        : this(executable, home, preview: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CopilotManagedSettingsReader"/> class that gives, for every account, what
    /// the runtime would enforce for a document of managed settings given as the policy of the organization
    /// (<c>managedSettings.compose</c>): the check of the developer instance, without an enterprise.
    /// </summary>
    /// <param name="executable">The runtime's entry point.</param>
    /// <param name="home">The folder the runtime keeps its state in.</param>
    /// <param name="preview">A <c>managed-settings.json</c> taken as the server policy of every account; null to resolve them.</param>
    /// <exception cref="ArgumentException">The entry point or the home is blank.</exception>
    public CopilotManagedSettingsReader(string executable, string home, string? preview)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        _executable = executable;
        _home = home;
        _preview = string.IsNullOrWhiteSpace(preview) ? null : preview;
    }

    /// <summary>
    /// Resolves the managed settings of each GitHub token, and those of the computer alone when none is given. The cache
    /// of the runtime's own home is cleared first: an answer that is not <see cref="CopilotManagedSettings.FailClosed"/>
    /// then comes from a live fetch, never from a policy the runtime kept, and CodeAlta keeps its own for when GitHub
    /// cannot be reached. A token that cannot be resolved, or that the runtime resolves to no account, gives settings
    /// marked <see cref="CopilotManagedSettings.FailClosed"/> with the reason, without failing the other tokens.
    /// </summary>
    /// <param name="gitHubTokens">The tokens, each the sign-in of a Copilot provider; empty for the computer alone.</param>
    /// <param name="cancellationToken">Stops the reading and the runtime.</param>
    /// <returns>The settings of each token, in the same order, or the one of the computer when no token is given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gitHubTokens"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The runtime could not be started, or refused a request.</exception>
    /// <exception cref="OperationCanceledException">The reading was canceled.</exception>
    public async Task<IReadOnlyList<CopilotManagedSettings>> ReadAsync(IReadOnlyList<string> gitHubTokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitHubTokens);
        Directory.CreateDirectory(_home);
        var client = new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(_executable),
            BaseDirectory = _home,
            WorkingDirectory = _home,
            UseLoggedInUser = false,
        });
        await using (client.ConfigureAwait(false))
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            // A policy the runtime kept could stand in for a failed fetch without saying so in its answer: none is kept.
            await client.Rpc.ManagedSettings.ClearCacheAsync(cancellationToken).ConfigureAwait(false);
            var settings = new List<CopilotManagedSettings>(Math.Max(1, gitHubTokens.Count));
            // The document of a check is added to what the accounts resolve, never in their place: it only narrows.
            CopilotManagedSettings? preview = null;
            if (_preview is { } path)
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                preview = CopilotManagedSettings.From(await client.Rpc.ManagedSettings.ComposeAsync(
                    [new ManagedSettingsComposeLayer { Source = ManagedSettingsChannel.Server, Settings = document.RootElement.Clone() }], cancellationToken).ConfigureAwait(false));
            }

            if (gitHubTokens.Count == 0)
            {
                settings.Add(CopilotManagedSettings.From(await client.Rpc.ManagedSettings.ResolveAsync(clientName: ClientName, cancellationToken: cancellationToken).ConfigureAwait(false)));
            }

            foreach (var token in gitHubTokens)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(token, nameof(gitHubTokens));
                try
                {
                    var resolved = CopilotManagedSettings.From(await client.Rpc.ManagedSettings.ResolveAsync(gitHubToken: token.Trim(), clientName: ClientName,
                        cancellationToken: cancellationToken).ConfigureAwait(false));
                    // Without an account, only the computer was read: the policy of the account is not known.
                    settings.Add(resolved.Account is null ? resolved with { FailClosed = true, Diagnostics = [.. resolved.Diagnostics, "The account of a token could not be resolved."] } : resolved);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // An expired token of one provider does not keep the policies of the others from applying.
                    settings.Add(new CopilotManagedSettings { FailClosed = true, Diagnostics = [$"The policy of an account could not be read: {exception.Message}"] });
                }
            }

            return preview is null ? settings : [.. settings.Select(resolved => resolved.Strictest(preview))];
        }
    }
}
