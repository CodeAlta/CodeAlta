using System.Text;

namespace CodeAlta.Fleet.Update.Service;

/// <summary>
/// Reads the password of the account of a Windows service for <c>install --password-stdin</c>: the first line of standard
/// input when it is redirected, else typed at a prompt that does not show it, so that it stays out of the scrollback.
/// </summary>
public static class ServicePassword
{
    /// <summary>Reads the password.</summary>
    /// <returns>The password; null when none was given or the prompt was left with Escape.</returns>
    public static async Task<string?> ReadAsync()
    {
        if (Console.IsInputRedirected) return await Console.In.ReadLineAsync().ConfigureAwait(false);
        Console.Error.Write("Password of the account (not shown): ");
        var text = new StringBuilder();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                Console.Error.WriteLine();
                return null;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return text.ToString();
    }
}
