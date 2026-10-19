using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Cursor;

/// <summary>
/// Browser and device sign-in through the Cursor agent CLI, and the status text that CLI prints.
/// </summary>
public static class CursorAgentLogin
{
    private static readonly Regex UrlPattern = new(@"https://[^\s<>""']+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UserCodePattern = new(@"user\s*code\s*[:=]\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Runs <c>agent login</c>. Device mode sets <c>NO_OPEN_BROWSER=1</c> and does not open a browser.
    /// </summary>
    /// <param name="launch">The CLI launch.</param>
    /// <param name="deviceFlow"><see langword="true"/> for the device form.</param>
    /// <param name="onPrompt">Receives the https URL and, when the CLI prints one, the user code.</param>
    /// <param name="cancellationToken">Cancels the login and the prompt.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The CLI prints no https URL or exits with a failure.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async Task LoginAsync(CursorCliLaunch launch, bool deviceFlow, Func<Uri, string?, CancellationToken, ValueTask> onPrompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(onPrompt);
        using var process = CursorProcess.Start(launch, "login", deviceFlow);
        var output = new StringBuilder();
        var gate = new object();
        var prompt = new TaskCompletionSource<(Uri Url, string? UserCode)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? delay = null;

        void Consider(bool allowMissingCode)
        {
            string text;
            lock (gate)
            {
                text = output.ToString();
            }

            var url = FindUrl(text);
            if (url is null)
            {
                return;
            }

            var code = FindUserCode(text);
            if (deviceFlow && code is null && !allowMissingCode)
            {
                delay ??= DelayPromptAsync();
                return;
            }

            prompt.TrySetResult((url, deviceFlow ? code : null));
        }

        async Task DelayPromptAsync()
        {
            try
            {
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Consider(allowMissingCode: true);
        }

        async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lock (gate)
                {
                    output.AppendLine(line);
                }

                Consider(allowMissingCode: false);
            }
        }

        try
        {
            var pumps = Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError));
            var exited = process.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(prompt.Task, exited).ConfigureAwait(false);
            if (first != prompt.Task)
            {
                await pumps.ConfigureAwait(false);
                Consider(allowMissingCode: true);
            }

            if (!prompt.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("Cursor login did not print an https URL. " + Tail(output));
            }

            var shown = await prompt.Task.ConfigureAwait(false);
            await onPrompt(shown.Url, shown.UserCode, cancellationToken).ConfigureAwait(false);
            await exited.ConfigureAwait(false);
            await pumps.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Cursor login exited with code {process.ExitCode}. {Tail(output)}");
            }
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
        finally
        {
            Stop(process);
        }
    }

    /// <summary>
    /// Runs <c>agent status</c> and reports whether that text says the CLI is already signed in.
    /// </summary>
    /// <param name="launch">The CLI launch.</param>
    /// <param name="cancellationToken">Cancels the status process.</param>
    /// <returns><see langword="true"/> when the CLI reports an existing login.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launch"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async Task<bool> IsAuthenticatedAsync(CursorCliLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        using var process = CursorProcess.Start(launch, "status", deviceFlow: false);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            return IsAuthenticatedText(text);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
        finally
        {
            Stop(process);
        }
    }

    /// <summary>
    /// Interprets CLI status text. Negative phrases win, so "not logged in" is signed out.
    /// </summary>
    /// <param name="text">The combined status output.</param>
    /// <returns><see langword="true"/> when the text reports a login.</returns>
    public static bool IsAuthenticatedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var sample = text.ToLowerInvariant();
        if (sample.Contains("not logged in", StringComparison.Ordinal)
            || sample.Contains("not authenticated", StringComparison.Ordinal)
            || sample.Contains("signed out", StringComparison.Ordinal)
            || sample.Contains("login required", StringComparison.Ordinal))
        {
            return false;
        }

        return sample.Contains("logged in", StringComparison.Ordinal)
            || sample.Contains("authenticated", StringComparison.Ordinal)
            || sample.Contains("signed in", StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs <c>agent models</c> and returns the ids and names that command prints.
    /// </summary>
    /// <param name="launch">The CLI launch.</param>
    /// <param name="cancellationToken">Cancels the process.</param>
    /// <returns>The models of the signed-in account, in the order the CLI printed them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launch"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The CLI exits without printing a model.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CursorCliLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        using var process = CursorProcess.Start(launch, "models", deviceFlow: false);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            var models = ParseModels(text);
            if (process.ExitCode != 0 && models.Count == 0)
            {
                var error = (await stderr.ConfigureAwait(false)).Trim();
                throw new InvalidOperationException(error.Length == 0 ? "The Cursor agent CLI did not list models." : error);
            }

            return models;
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
        finally
        {
            Stop(process);
        }
    }

    /// <summary>
    /// Reads <c>agent models</c> text. A model line is <c>id - display name</c>. Other lines are skipped.
    /// </summary>
    /// <param name="text">The command output.</param>
    /// <returns>The models, in order.</returns>
    public static IReadOnlyList<AgentModelInfo> ParseModels(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var models = new List<AgentModelInfo>();
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var id = line[..separator].Trim();
            var name = line[(separator + 3)..].Trim();
            if (id.Length == 0 || name.Length == 0 || id.Contains(' '))
            {
                continue;
            }

            models.Add(new AgentModelInfo(id, name, Provider: CursorModelProviderRuntime.ProviderType));
        }

        return models;
    }

    private static Uri? FindUrl(string text)
    {
        var match = UrlPattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Value.TrimEnd('.', ',', ';', ')', ']');
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private static string? FindUserCode(string text)
    {
        var match = UserCodePattern.Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string Tail(StringBuilder output)
    {
        var text = output.ToString().Trim();
        return text.Length == 0 ? "The CLI produced no output." : text;
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
