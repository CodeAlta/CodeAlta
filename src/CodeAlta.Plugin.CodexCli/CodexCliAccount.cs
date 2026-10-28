using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Protocol;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>
/// The sign-in of a Codex CLI provider, run by CodeAlta through a short-lived app-server in the home of the provider:
/// <c>account/login/start</c> is Codex's own sign-in, the one ChatGPT Business workspaces accept. Codex keeps the token
/// in the home; CodeAlta never reads it.
/// </summary>
internal sealed class CodexCliAccount(CodexCliPlugin plugin) : IPluginProviderAccount
{
    /// <summary>The method that signs in with ChatGPT in the browser.</summary>
    public const string BrowserMethod = "chatgpt";

    /// <summary>The method that signs in with ChatGPT and a device code.</summary>
    public const string DeviceMethod = "chatgpt-device";

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(15);

    public Task<IReadOnlyList<PluginProviderSignInMethod>> GetSignInMethodsAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PluginProviderSignInMethod>>(
        [
            new PluginProviderSignInMethod(BrowserMethod, "Sign in with ChatGPT", "Opens the ChatGPT sign-in of Codex in the browser. It works for ChatGPT Business and Enterprise workspaces."),
            new PluginProviderSignInMethod(DeviceMethod, "Sign in with a device code", "Shows a code to enter on the ChatGPT sign-in page, for a computer whose browser cannot return to CodeAlta."),
        ]);

    public Task SignInAsync(PluginProviderSettings settings, string methodId, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The sign-in of Codex shows an address to open: sign in from Settings > Providers in CodeAlta Desktop.");

    public async Task SignInAsync(PluginProviderSettings settings, string methodId, Func<PluginProviderSignInPrompt, CancellationToken, ValueTask> onPrompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onPrompt);
        if (methodId is not (BrowserMethod or DeviceMethod))
        {
            throw new InvalidOperationException($"Codex has no sign-in '{methodId}'.");
        }

        var options = plugin.CreateOptions(settings, toolEndpoints: null, logger: plugin.Logger, catalog: null);
        await SignInAsync(options, methodId == DeviceMethod, onPrompt, plugin.Logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs a provider in: starts an app-server in its home, shows the prompt, and waits for Codex to report the end.</summary>
    /// <exception cref="InvalidOperationException">The sign-in failed; the message says why.</exception>
    internal static async Task SignInAsync(CodexCliRuntimeOptions options, bool deviceCode, Func<PluginProviderSignInPrompt, CancellationToken, ValueTask> onPrompt, Logger? logger,
        CancellationToken cancellationToken)
    {
        CodexAppServerCommand command;
        try
        {
            command = await options.ResolveCommandAsync(progress: null, cancellationToken).ConfigureAwait(false);
        }
        catch (CodeAlta.Agent.Runtime.AgentProviderFailureException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        var completed = new TaskCompletionSource<AccountLoginCompletedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        CodexAppServerClient? client = null;
        try
        {
            Directory.CreateDirectory(options.ProbeDirectory);
            CodexCliHomeConfig.Write(options.Home);
            client = await options.ConnectionFactory.StartAsync(
                    new CodexCliConnectionRequest(command, options.Home, options.Settings.Environment, options.ProbeDirectory, options.InitializeTimeout),
                    message =>
                    {
                        if (message.RequestId is { } requestId)
                        {
                            _ = client?.RespondErrorAsync(requestId, CodexErrorCodes.MethodNotFound, "CodeAlta is signing in.");
                        }
                        else if (message.Method == CodexMethods.AccountLoginCompleted &&
                                 CodexMethods.ParseNotification(message.Method, message.Params) is AccountLoginCompletedNotification notification)
                        {
                            completed.TrySetResult(notification);
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var started = await client.AccountLoginStartAsync(deviceCode ? new LoginAccountParams.ChatgptDeviceCodev2() : new LoginAccountParams.Chatgptv2(), cancellationToken)
                .ConfigureAwait(false);
            var (loginId, prompt) = started switch
            {
                LoginAccountResponse.Chatgptv2 browser => (browser.LoginId, new PluginProviderSignInPrompt(new Uri(browser.AuthUrl), null, null)),
                LoginAccountResponse.ChatgptDeviceCodev2 device => (device.LoginId, new PluginProviderSignInPrompt(new Uri(device.VerificationUrl), device.UserCode, DateTimeOffset.UtcNow.Add(SignInTimeout))),
                _ => throw new InvalidOperationException("Codex did not start a ChatGPT sign-in."),
            };

            try
            {
                await onPrompt(prompt, cancellationToken).ConfigureAwait(false);
                var result = await completed.Task.WaitAsync(SignInTimeout, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    throw new InvalidOperationException($"The sign-in to ChatGPT did not complete{(string.IsNullOrWhiteSpace(result.Error) ? "." : $": {result.Error}")}");
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                // The sign-in is given up: Codex stops waiting for the browser, and frees its port.
                try
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await client.AccountLoginCancelAsync(new CancelLoginAccountParams { LoginId = loginId }, cancel.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is CodexJsonRpcException or CodexConnectionClosedException or OperationCanceledException or TimeoutException)
                {
                    logger?.Debug($"The sign-in of Codex could not be cancelled: {ex.Message}");
                }

                if (exception is TimeoutException)
                {
                    throw new InvalidOperationException("The sign-in to ChatGPT took too long: start it again.", exception);
                }

                throw;
            }
        }
        catch (CodexJsonRpcException exception)
        {
            throw new InvalidOperationException($"Codex could not start the sign-in: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is CodexConnectionClosedException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Codex could not be started for the sign-in. {exception.Message}", exception);
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
