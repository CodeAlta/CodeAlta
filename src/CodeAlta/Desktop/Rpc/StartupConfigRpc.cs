using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Repairs a global <c>config.toml</c> that cannot be loaded, before the host exists: the window shows the
/// file in an editor, validates it as it is typed and starts the application once a valid file is saved.
/// It only reads, validates and writes that one file; no provider, plugin or session is touched.
/// </summary>
[NeoRpcService("startupConfig", Version = 1)]
internal sealed class StartupConfigService
{
    /// <summary>Largest configuration text accepted or returned, in UTF-16 units.</summary>
    internal const int MaximumContentLength = GlobalConfigService.MaximumContentLength;

    private const int MaximumMessageLength = 512;
    private readonly ConfigRecoveryService _recovery;
    private readonly Lock _gate = new(); // The recovery document takes one operation at a time.
    private bool _settled;

    /// <summary>Creates the service over a recovery document that has been read once.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="recovery"/> is null.</exception>
    internal StartupConfigService(ConfigRecoveryService recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        _recovery = recovery;
    }

    /// <summary>Called once, after a valid configuration was saved: the application can start.</summary>
    internal Action? Continue { get; init; }

    /// <summary>Called once when the user leaves without repairing the file.</summary>
    internal Action? Exit { get; init; }

    /// <summary>The configuration as it was last read or saved, and why it cannot be used.</summary>
    [NeoRpcMethod("read")]
    public StartupConfigDocument Read(StartupConfigRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate) return Document();
    }

    /// <summary>Reads the file again, discarding what the editor holds.</summary>
    [NeoRpcMethod("reload")]
    public StartupConfigDocument Reload(StartupConfigRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_settled) _recovery.Reload();
            return Document();
        }
    }

    /// <summary>Validates configuration text without writing it.</summary>
    [NeoRpcMethod("validate")]
    public StartupConfigValidation Validate(StartupConfigContentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content is null || request.Content.Length > MaximumContentLength)
            return new(false, "The configuration exceeds the editor limit.", null, null);
        return Validation(_recovery.Validate(request.Content));
    }

    /// <summary>
    /// Saves valid text over the file that was read and lets the application start. <c>invalid</c>, <c>failed</c>
    /// (the file changed on disk, or could not be written) and <c>too_large</c> leave the file and the window as
    /// they are.
    /// </summary>
    [NeoRpcMethod("save")]
    public StartupConfigSaveResponse Save(StartupConfigContentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content is null || request.Content.Length > MaximumContentLength) return new("too_large", null, null);
        lock (_gate)
        {
            if (_settled) return new("ok", null, null);
            var validation = _recovery.Validate(request.Content);
            if (!validation.IsValid) return new("invalid", null, Validation(validation));
            if (!_recovery.Save(request.Content)) return new("failed", Bound(_recovery.Failure), null);
            _settled = true;
        }
        Continue?.Invoke();
        return new("ok", null, null);
    }

    /// <summary>Leaves the application without changing the file.</summary>
    [NeoRpcMethod("exit")]
    public StartupConfigExitResponse Leave(StartupConfigRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_settled) return new("ok");
            _settled = true;
        }
        Exit?.Invoke();
        return new("ok");
    }

    private StartupConfigDocument Document()
    {
        var snapshot = _recovery.Snapshot;
        if (snapshot is null) return new("unreadable", _recovery.ConfigPath, null, Bound(_recovery.Failure), null);
        return snapshot.Text.Length > MaximumContentLength
            ? new("too_large", _recovery.ConfigPath, null, null, null)
            : new("ok", _recovery.ConfigPath, snapshot.Text, Bound(_recovery.Failure), Validation(_recovery.Validate(snapshot.Text)));
    }

    private static StartupConfigValidation Validation(CodeAltaConfigValidationResult result)
        => new(result.IsValid, Bound(result.Message), result.Line, result.Column);

    private static string? Bound(string? message)
        => message is { Length: > MaximumMessageLength } ? message[..MaximumMessageLength] : message;
}

internal sealed record StartupConfigRequest;

/// <summary>Complete configuration text from the editor.</summary>
internal sealed record StartupConfigContentRequest(string? Content);

/// <summary>
/// The configuration file. <c>ok</c> carries its text; <c>unreadable</c> has none (the file could not be read
/// or decoded, see <see cref="Failure"/>) and <c>too_large</c> is beyond the editor limit.
/// </summary>
internal sealed record StartupConfigDocument(string Status, string Path, string? Content, string? Failure, StartupConfigValidation? Validation);

/// <summary>Whether the text is a usable configuration, and where it is not.</summary>
internal sealed record StartupConfigValidation(bool Valid, string? Message, int? Line, int? Column);

/// <summary><c>ok</c>, <c>invalid</c>, <c>failed</c> or <c>too_large</c>.</summary>
internal sealed record StartupConfigSaveResponse(string Status, string? Failure, StartupConfigValidation? Validation);

internal sealed record StartupConfigExitResponse(string Status);
