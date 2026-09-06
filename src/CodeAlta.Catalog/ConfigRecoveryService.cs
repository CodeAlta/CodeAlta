using System.Text;

namespace CodeAlta.Catalog;

/// <summary>
/// Owns one startup recovery document at an explicitly captured global root. Reads and validation
/// do not discover providers, plugins, sessions or environment credentials. Call operations serially.
/// </summary>
/// <remarks>
/// Only a successful read or save supplies a save baseline. Conflicts never adopt the observed
/// revision; explicitly reload and reapply edits instead. The shared codec is not cross-process CAS.
/// </remarks>
public sealed class ConfigRecoveryService
{
    private readonly TextFileCodec _textFiles;
    private readonly CodeAltaConfigStore _store;

    /// <summary>Captures a trusted absolute global root and the codec shared by this recovery workflow.</summary>
    /// <exception cref="ArgumentException">The root is blank, relative or invalid.</exception>
    /// <exception cref="ArgumentNullException">The codec is null.</exception>
    public ConfigRecoveryService(string globalRoot, TextFileCodec textFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRoot);
        ArgumentNullException.ThrowIfNull(textFiles);
        if (!Path.IsPathFullyQualified(globalRoot)) throw new ArgumentException("An absolute global root is required.", nameof(globalRoot));
        _textFiles = textFiles;
        _store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.GetFullPath(globalRoot) }, textFiles);
    }

    /// <summary>Gets the captured file path, not a renderer access grant.</summary>
    public string ConfigPath => _store.ConfigPath;

    /// <summary>Gets the actual loaded/acknowledged snapshot, or null when the read baseline is unknown.</summary>
    public TextFileSnapshot? Snapshot { get; private set; }

    /// <summary>Gets the last storage/recovery failure separately from editor syntax validity.</summary>
    public string? Failure { get; private set; }

    /// <summary>Gets whether this workflow created the first-run defaults.</summary>
    public bool CreatedDefault { get; private set; }

    /// <summary>Gets whether the current acknowledged document permits startup.</summary>
    public bool IsReady => Snapshot is not null && Failure is null && Validate(Snapshot.Text).IsValid;

    /// <summary>Validates complete TOML with the existing config-owner semantics, without I/O.</summary>
    public CodeAltaConfigValidationResult Validate(string text)
        => CodeAltaConfigStore.ValidateGlobalConfigContent(text, ConfigPath);

    /// <summary>Explicitly reloads (or conditionally creates missing defaults). Read failures revoke the baseline.</summary>
    /// <returns>True when a complete snapshot was read, even if its TOML is invalid.</returns>
    public bool Reload() => ReloadAsync().GetAwaiter().GetResult();

    /// <summary>Reloads complete strict Unicode text, validating competing first-run creation before startup.</summary>
    /// <returns>True when a complete snapshot was read. Expected read/create/decode failures are exposed in <see cref="Failure"/>.</returns>
    public async Task<bool> ReloadAsync()
    {
        Snapshot = null;
        Failure = null;
        try
        {
            try
            {
                Snapshot = await _textFiles.LoadAsync(ConfigPath).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                CreatedDefault |= await _store.EnsureGlobalConfigExistsAsync().ConfigureAwait(false);
                // Even our own creation is reloaded; a competing creator is never admitted blindly.
                Snapshot = await _textFiles.LoadAsync(ConfigPath).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidOperationException)
        {
            Failure = SR.T("Unable to load or create config file: {0}", ex.Message);
            return false;
        }
    }

    /// <summary>Saves complete valid text against only the loaded/acknowledged revision, preserving encoding and BOM.</summary>
    /// <returns>True only on acknowledged save. Failures retain the old baseline and are exposed in <see cref="Failure"/>.</returns>
    public bool Save(string text)
    {
        var snapshot = Snapshot;
        if (snapshot is null)
        {
            Failure = SR.T("Reload the config successfully before saving. No readable baseline is available.");
            return false;
        }
        var validation = Validate(text);
        if (!validation.IsValid)
        {
            Failure = validation.Message;
            return false;
        }
        try
        {
            var result = _textFiles.Save(new TextFileSaveRequest(ConfigPath, text, snapshot.Encoding,
                snapshot.HasByteOrderMark, snapshot.Revision));
            if (result.IsConflict)
            {
                Failure = SR.T("Config changed on disk. Reload and reapply your edits; startup has not continued.");
                return false;
            }
            Snapshot = result.Snapshot!;
            Failure = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            Failure = SR.T("Unable to save config file: {0}", ex.Message);
            return false;
        }
    }
}
