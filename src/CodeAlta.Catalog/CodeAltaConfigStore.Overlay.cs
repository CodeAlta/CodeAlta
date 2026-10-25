using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace CodeAlta.Catalog;

// The user's configuration as CodeAltaConfigOptions has the store read it: the file, or nothing when it is ignored, then
// the overlays merged over it; a strict reading; no write in a read-only process.
public sealed partial class CodeAltaConfigStore
{
    /// <summary>Gets how this store reads and writes the configuration (<see cref="CatalogOptions.Config"/>).</summary>
    public CodeAltaConfigOptions ConfigOptions => _config;

    /// <summary>Gets whether this store never writes a configuration file (<see cref="CodeAltaConfigOptions.ReadOnly"/>).</summary>
    public bool IsReadOnly => _config.ReadOnly;

    /// <summary>
    /// Validates the configuration this store reads: the user's file (unless ignored), then the overlays, with the
    /// strict reading when it is asked for, without starting providers or sessions.
    /// </summary>
    /// <returns>
    /// The result; its message names what makes the configuration invalid: the file, <c>CODEALTA_CONFIG_CONTENT</c> or a
    /// <c>-c</c> setting. Its line and column are given for an error of the file only.
    /// </returns>
    public CodeAltaConfigValidationResult ValidateEffectiveGlobalConfig()
    {
        try
        {
            var layers = GlobalLayers(ReadGlobalFileText());
            var document = ParseGlobal(layers);
            try
            {
                ValidateGlobalDocument(CodeAltaConfigDocumentCopy.Copy(document));
            }
            catch (Exception ex) when (IsConfigLoadException(ex) && layers.Count > 1)
            {
                throw Attribute(layers, ex, static (text, source) => ValidateGlobalDocument(ParseDocument(text, source)));
            }

            return document.UnsupportedProviders.Count == 0
                ? CodeAltaConfigValidationResult.Valid
                : CodeAltaConfigValidationResult.Valid with { Warning = DescribeUnsupportedProviders(document.UnsupportedProviders) };
        }
        catch (InvalidDataException ex) when (IsOverlayFailure(ex))
        {
            return new CodeAltaConfigValidationResult(false, ex.Message, null, null);
        }
        catch (Exception ex) when (IsConfigLoadException(ex) || ex is UnauthorizedAccessException)
        {
            return CreateValidationFailure(ex);
        }
    }

    private bool ChangesGlobalContent => _config.IgnoreUserConfig || _config.Overlays.Count > 0;

    // Every save of the store starts here: a read-only process writes no configuration file.
    private void ThrowIfReadOnly()
    {
        if (_config.ReadOnly)
        {
            throw new InvalidOperationException(
                $"The CodeAlta configuration is read-only in this process: '{_options.ConfigPath}' and the project configurations are not written.");
        }
    }

    // The user's configuration as the reads of the store see it, shared by them: never changed, never handed out.
    private CodeAltaConfigDocument ReadSharedGlobalDocument()
    {
        var path = _options.ConfigPath;
        return _config.IgnoreUserConfig || !File.Exists(path)
            ? _overlaysOnly.Value
            : _globalDocuments.Read(path, _parseGlobalFile);
    }

    private CodeAltaConfigDocument LoadOwnedGlobalDocument()
        => CodeAltaConfigDocumentCopy.Copy(ReadSharedGlobalDocument());

    // The text of the user's file, or null when it is ignored or does not exist. An inaccessible file is an error, never
    // taken for a missing one.
    private string? ReadGlobalFileText()
    {
        if (_config.IgnoreUserConfig) return null;
        try
        {
            return SharedTextFile.ReadAllText(_options.ConfigPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    // The layers of the user's configuration: the file when it says something, then the overlays.
    private List<ConfigLayer> GlobalLayers(string? fileContent)
    {
        var layers = new List<ConfigLayer>(_config.Overlays.Count + 1);
        if (!string.IsNullOrWhiteSpace(fileContent))
        {
            layers.Add(new ConfigLayer($"'{_options.ConfigPath}'", _options.ConfigPath, fileContent, IsFile: true));
        }

        foreach (var overlay in _config.Overlays)
        {
            layers.Add(new ConfigLayer(overlay.Source, overlay.Source, overlay.Content, IsFile: false));
        }

        return layers;
    }

    // The text the user's configuration reads as: the file as it is without overlays, else the layers merged.
    private static string ComposeText(IReadOnlyList<ConfigLayer> layers, int count)
    {
        if (count == 1 && layers[0].IsFile) return layers[0].Text;
        var merged = new TomlTable();
        for (var index = 0; index < count; index++)
        {
            CodeAltaConfigOverlay.Merge(merged, layers[index].Parse());
        }

        return merged.Count == 0 ? string.Empty : TomlSerializer.Serialize(merged);
    }

    private CodeAltaConfigDocument ParseGlobal(IReadOnlyList<ConfigLayer> layers)
    {
        if (layers.Count == 0) return new CodeAltaConfigDocument();
        CodeAltaConfigDocument document;
        if (layers.Count == 1 && layers[0].IsFile)
        {
            document = ParseLoadedContent(_options.ConfigPath, layers[0].Text);
        }
        else
        {
            var text = ComposeText(layers, layers.Count);
            try
            {
                document = ParseDocument(text, _options.ConfigPath);
            }
            catch (Exception ex) when (IsConfigLoadException(ex))
            {
                throw Attribute(layers, ex, static (text, source) => _ = ParseDocument(text, source));
            }
        }

        if (_config.Strict)
        {
            RejectUnknownSettings(layers);
        }

        return document;
    }

    // A project file, read as the user's file is: with the strict reading when it is asked for.
    private CodeAltaConfigDocument ParseFile(string path, string content)
    {
        var document = ParseLoadedContent(path, content);
        if (_config.Strict && !string.IsNullOrWhiteSpace(content))
        {
            RejectUnknownSettings([new ConfigLayer($"'{path}'", path, content, IsFile: true)]);
        }

        return document;
    }

    // Names the layer that makes the configuration fail: the last one whose layers before it still read. The check runs
    // again on the layers up to it, with the source of that layer in its diagnostics.
    private InvalidDataException Attribute(IReadOnlyList<ConfigLayer> layers, Exception failure, Action<string, string> check)
    {
        for (var count = layers.Count - 1; count >= 0; count--)
        {
            if (count > 0 && !Succeeds(ComposeText(layers, count), layers[count - 1].SourceName, check)) continue;
            var blamed = layers[count];
            Exception cause = failure;
            try
            {
                check(ComposeText(layers, count + 1), blamed.SourceName);
            }
            catch (Exception ex) when (IsConfigLoadException(ex))
            {
                cause = ex;
            }

            return blamed.IsFile
                ? ConfigLayerException(true, $"Failed to parse CodeAlta config '{_options.ConfigPath}'.", cause)
                : ConfigLayerException(false, $"The CodeAlta configuration given by {blamed.Source} is not valid: {DescribeFailure(cause)}", cause);
        }

        return ConfigLayerException(true, $"Failed to parse CodeAlta config '{_options.ConfigPath}'.", failure);

        static bool Succeeds(string text, string source, Action<string, string> check)
        {
            try
            {
                check(text, source);
                return true;
            }
            catch (Exception ex) when (IsConfigLoadException(ex))
            {
                return false;
            }
        }
    }

    private static string DescribeFailure(Exception exception)
        => (FindTomlException(exception)?.Message ?? exception.GetBaseException().Message).Trim();

    // The strict reading: every setting of the merged layers must be one the typed document reads, except what other
    // owners read (plugin tables, spaces lists, desktop automations, providers of a type a plugin runs). Each unknown
    // setting is named with the last layer that writes it, the one that gives its value.
    private void RejectUnknownSettings(IReadOnlyList<ConfigLayer> layers)
    {
        var merged = new TomlTable();
        foreach (var layer in layers) CodeAltaConfigOverlay.Merge(merged, layer.Parse());
        if (merged.Count == 0) return;
        var read = TomlSerializer.Deserialize<CodeAltaConfigDocument>(TomlSerializer.Serialize(merged)) ?? new CodeAltaConfigDocument();
        var external = FindUnsupportedProviders(read).Select(static entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var unknown = new List<string[]>();
        FindUnknownSettings(merged, ToTable(read), [], unknown, external);
        if (unknown.Count == 0) return;

        var tables = layers.Select(static layer => layer.Parse()).ToArray();
        var blamed = unknown.Select(path => (Path: path, Layer: layers[Math.Max(0, Array.FindLastIndex(tables, table => Contains(table, path)))])).ToArray();
        throw ConfigLayerException(blamed.All(static entry => entry.Layer.IsFile),
            $"Unknown settings in the CodeAlta configuration: {string.Join(", ", blamed.Select(static entry => $"{FormatKeyPath(entry.Path)} ({entry.Layer.Source})"))}.", null);

        static bool Contains(TomlTable table, string[] path)
        {
            object? current = table;
            foreach (var key in path)
            {
                if (current is not TomlTable level || CodeAltaConfigOverlay.FindKey(level, key) is not { } found) return false;
                current = level[found];
            }

            return true;
        }
    }

    private static void FindUnknownSettings(TomlTable written, TomlTable modeled, string[] path, List<string[]> unknown, HashSet<string> externalProviders)
    {
        foreach (var (key, value) in written)
        {
            string[] keyPath = [.. path, key];
            if (IsOwnedElsewhere(path, key, externalProviders) || value is TomlTable { Count: 0 }) continue;
            if (!modeled.TryGetValue(key, out var model))
            {
                unknown.Add(keyPath);
                continue;
            }

            if (value is TomlTable table && model is TomlTable modeledTable)
            {
                FindUnknownSettings(table, modeledTable, keyPath, unknown, externalProviders);
            }
            else if (value is TomlTableArray tables && model is TomlTableArray modeledTables)
            {
                for (var index = 0; index < Math.Min(tables.Count, modeledTables.Count); index++)
                {
                    FindUnknownSettings(tables[index], modeledTables[index], keyPath, unknown, externalProviders);
                }
            }
        }

        static bool IsOwnedElsewhere(string[] path, string key, HashSet<string> externalProviders)
            => path.Length switch
            {
                // [automations.*] belongs to the desktop; [prompts] holds only the spaces of the prompts.
                0 => key is "automations" || key is "prompts",
                // A plugin reads its own table, and a plugin that runs a provider type reads that provider's section.
                1 => path[0] is "plugins" || (path[0] is "providers" && externalProviders.Contains(key)) || key is "spaces",
                _ => key is "spaces",
            };
    }

    private static string FormatKeyPath(string[] path)
        => string.Join('.', path.Select(static key => BareKey().IsMatch(key) ? key : "\"" + key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex BareKey();

    // One part of the user's configuration: the file or an overlay. Source names it in messages; SourceName in the
    // diagnostics of the TOML parser.
    private sealed record ConfigLayer(string Source, string SourceName, string Text, bool IsFile)
    {
        // A table of its own each time: merging changes the tables it is given.
        public TomlTable Parse()
        {
            try
            {
                return TomlSerializer.Deserialize<TomlTable>(Text, TomlSerializerOptions.Default with { SourceName = SourceName }) ?? new TomlTable();
            }
            catch (Exception ex) when (IsConfigLoadException(ex))
            {
                throw IsFile
                    ? ConfigLayerException(true, $"Failed to parse CodeAlta config '{SourceName}'.", ex)
                    : ConfigLayerException(false, $"Failed to parse the CodeAlta configuration of {Source}: {DescribeFailure(ex)}", ex);
            }
        }
    }

    // A configuration that cannot be read, naming the part of it at fault. When an overlay is at fault, the line and
    // column of the diagnostics are not the user's, so the validation leaves them out.
    private static InvalidDataException ConfigLayerException(bool isFile, string message, Exception? inner)
    {
        var exception = new InvalidDataException(message, inner);
        exception.Data[OverlayFailureKey] = !isFile;
        return exception;
    }

    private static bool IsOverlayFailure(Exception exception) => exception.Data[OverlayFailureKey] is true;

    private const string OverlayFailureKey = "CodeAlta.ConfigOverlayFailure";
}
