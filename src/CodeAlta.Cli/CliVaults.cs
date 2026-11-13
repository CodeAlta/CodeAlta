using System.Text;
using CodeAlta.Credentials;

namespace CodeAlta.Cli;

/// <summary>
/// How <c>altacli</c> treats a provider whose key is kept in a vault that cannot give it now. By default it asks no vault: a
/// command line often runs with nobody there, so the provider is not ready and its message names the secret, the vault and
/// the option. With <c>--interactive-vault</c> a run that needs the provider asks the vault to unlock, by the prompt of the
/// vault itself or, for a vault that takes a password, by a question on the terminal.
/// </summary>
internal static class CliVaults
{
    /// <summary>The option that says someone is there to answer a vault.</summary>
    public const string Option = "--interactive-vault";

    /// <summary>Makes the waiter of the providers that wait for a vault.</summary>
    /// <param name="broker">The credential broker of the host.</param>
    /// <param name="interactive">Whether <c>--interactive-vault</c> was given.</param>
    /// <returns>The waiter.</returns>
    public static VaultSecretWaiter Waiter(CredentialBroker broker, bool interactive)
        => new(broker, new VaultSecretWaiterOptions
        {
            WhenLocked = interactive
                ? static vault => $"altacli asked {vault} to unlock, and it did not."
                : static vault => $"altacli asks no vault unless told to: run it again with {Option}, with someone there to answer {vault}.",
            WhenAskedBeforeUse = "altacli cannot ask for that confirmation: turn off Ask before use for this secret in Settings › Secrets, or give the key another way (api_key_env).",
            SaysWhatCannotBeRead = true,
            AskPassword = interactive ? static (vault, _) => ValueTask.FromResult(AskPassword(vault.DisplayName)) : null,
            Asking = interactive ? static vault => Console.Error.WriteLine($"altacli: asking {vault.DisplayName} to unlock: answer its prompt.") : null,
        });

    // The password of a vault, typed on the terminal without being shown; null when the standard input is not a terminal.
    private static string? AskPassword(string vault)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"altacli: {vault} takes a password, and the standard input is not a terminal to type it on: give the prompt as words or with -f.");
            return null;
        }

        Console.Error.Write($"Password of {vault}: ");
        var password = new StringBuilder();
        try
        {
            while (Console.ReadKey(intercept: true) is { Key: not ConsoleKey.Enter } key)
            {
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0) password.Length--;
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The terminal went away while the password was typed.
            return null;
        }
        finally
        {
            Console.Error.WriteLine();
        }

        return password.ToString();
    }
}
