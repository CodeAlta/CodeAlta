namespace CodeAlta.Agent.OpenAI.Codex;

internal sealed class OpenAICodexSubscriptionHostIdProvider(string stateRootPath)
{
    public async ValueTask<string> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        // Separate from legacy Codex telemetry IDs: importing another tool's ID must
        // never change this runtime's identity or reuse another host's identity.
        var store = new FileOpenAICodexSubscriptionCredentialStore(stateRootPath);
        await using var lease = await store.AcquireLockAsync("agent-host", cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(stateRootPath, "installation", "openai-codex-subscription", "agent_host_id");
        if (File.Exists(path))
        {
            var value = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
            if (value.StartsWith("urn:uuid:", StringComparison.Ordinal) && Guid.TryParse(value[9..], out var id) && id != Guid.Empty)
            {
                return value;
            }

            throw new InvalidOperationException("The saved ChatGPT host ID is invalid. Restore it before signing in.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var hostId = "urn:uuid:" + Guid.NewGuid().ToString("D");
        var temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, hostId, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return hostId;
    }
}
