using System.Security.Cryptography;
using System.Text;
using CodeAlta.Catalog.Speech;

namespace CodeAlta.Desktop.Speech;

/// <summary>A Hugging Face repository a custom speech model is read from.</summary>
/// <param name="Repository">The repository, <c>owner/name</c>.</param>
/// <param name="Revision">The branch, tag or commit of the address, or null for the default branch.</param>
/// <param name="Folder">The folder of the files in the repository, empty for its root.</param>
internal sealed record SpeechRepositoryAddress(string Repository, string? Revision, string Folder);

/// <summary>
/// Reads the address of a custom speech model and recognizes, from the names of the files of a repository, how
/// the engine loads them. sherpa-onnx has no manifest for a model: its exports follow these names.
/// </summary>
internal static class SpeechModelSource
{
    /// <summary>The largest custom model accepted: nothing that fits a prompt bar is larger.</summary>
    public const long MaximumModelBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Reads <c>https://huggingface.co/owner/name</c>, with an optional <c>/tree/revision/folder</c>.</summary>
    public static bool TryParse(string? address, out SpeechRepositoryAddress repository)
    {
        repository = new("", null, "");
        if (address is null || SpeechSettings.GetModelError(address) is not null || !SpeechSettings.IsRepository(address)) return false;
        var segments = new Uri(address).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segments.Any(static segment => !IsSafeSegment(segment))) return false;
        if (segments.Length == 2)
        {
            repository = new(segments[0] + "/" + segments[1], null, "");
            return true;
        }

        // The page of a folder of a repository: /owner/name/tree/<revision>[/folder...].
        if (segments.Length < 4 || segments[2] != "tree") return false;
        repository = new(segments[0] + "/" + segments[1], segments[3], string.Join('/', segments.Skip(4)));
        return true;
    }

    /// <summary>
    /// Gets the folder name of a custom model: readable, made of the characters of a model id, and different
    /// for two addresses.
    /// </summary>
    public static string IdOf(string address)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        if (!TryParse(address, out var repository)) throw new ArgumentException("The address is not a Hugging Face repository.", nameof(address));
        var name = new StringBuilder("custom-");
        foreach (var character in repository.Repository.ToLowerInvariant())
        {
            name.Append(character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' ? character : '-');
            if (name.Length >= 72) break;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address)))[..8];
        return name.Append('-').Append(hash).ToString();
    }

    /// <summary>Gets the model of a configuration value: a preset id, or the id of a custom address. Null when neither.</summary>
    public static string? ModelIdOf(string? model)
        => SpeechModelCatalog.FindPreset(model) is { } preset ? preset.Id
            : model is not null && TryParse(model, out _) ? IdOf(model)
            : null;

    /// <summary>
    /// Recognizes a model in the files of a folder. The quantized (<c>int8</c>) file of a part is preferred.
    /// </summary>
    /// <param name="address">The address the user gave.</param>
    /// <param name="repository">The repository it names.</param>
    /// <param name="commit">The commit the files were listed at.</param>
    /// <param name="files">The files of the folder, with empty roles.</param>
    /// <returns>The model, or null when the files are not those of a model the engine loads here.</returns>
    public static SpeechModelDefinition? Classify(string address, SpeechRepositoryAddress repository, string commit, IReadOnlyList<SpeechFile> files)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(files);
        var usable = files.Where(static file => IsSafeSegment(file.Name) && file.Size > 0).ToArray();
        SpeechFile? Pick(string role, Func<string, bool> matches)
        {
            var candidates = usable.Where(file => matches(file.Name)).OrderBy(static file => file.Name, StringComparer.Ordinal).ToArray();
            var chosen = candidates.FirstOrDefault(static file => file.Name.Contains(".int8.", StringComparison.Ordinal)) ?? candidates.FirstOrDefault();
            return chosen is null ? null : chosen with { Role = role };
        }

        static bool Model(string name) => name.EndsWith(".onnx", StringComparison.Ordinal) || name.EndsWith(".ort", StringComparison.Ordinal);
        var tokens = Pick("tokens", static name => name == "tokens.txt");
        SpeechFile?[] parts;
        SpeechModelFamily family;
        if (tokens is not null && Pick("encoder", static name => name.StartsWith("encoder_model.", StringComparison.Ordinal) && Model(name)) is { } moonshineEncoder
            && Pick("mergedDecoder", static name => name.StartsWith("decoder_model_merged.", StringComparison.Ordinal) && Model(name)) is { } merged)
        {
            (family, parts) = (SpeechModelFamily.MoonshineV2, [moonshineEncoder, merged, tokens]);
        }
        else if (tokens is not null && Pick("preprocessor", static name => name == "preprocess.onnx") is { } preprocessor)
        {
            (family, parts) = (SpeechModelFamily.MoonshineV1,
            [
                preprocessor,
                Pick("encoder", static name => name.StartsWith("encode.", StringComparison.Ordinal) && Model(name)),
                Pick("uncachedDecoder", static name => name.StartsWith("uncached_decode.", StringComparison.Ordinal) && Model(name)),
                Pick("cachedDecoder", static name => name.StartsWith("cached_decode.", StringComparison.Ordinal) && Model(name)),
                tokens,
            ]);
        }
        else if (tokens is not null && Pick("joiner", static name => name.StartsWith("joiner", StringComparison.Ordinal) && Model(name)) is { } joiner)
        {
            var nemo = repository.Repository.Contains("nemo", StringComparison.OrdinalIgnoreCase) || repository.Repository.Contains("parakeet", StringComparison.OrdinalIgnoreCase);
            (family, parts) = (nemo ? SpeechModelFamily.NemoTransducer : SpeechModelFamily.Transducer,
            [
                Pick("encoder", static name => name.StartsWith("encoder", StringComparison.Ordinal) && Model(name)),
                Pick("decoder", static name => name.StartsWith("decoder", StringComparison.Ordinal) && Model(name)),
                joiner,
                tokens,
            ]);
        }
        else
        {
            (family, parts) = (SpeechModelFamily.Whisper,
            [
                Pick("encoder", static name => name.Contains("-encoder.", StringComparison.Ordinal) && Model(name)),
                Pick("decoder", static name => name.Contains("-decoder.", StringComparison.Ordinal) && Model(name)),
                Pick("tokens", static name => name.EndsWith("-tokens.txt", StringComparison.Ordinal)),
            ]);
        }

        if (parts.Any(static part => part is null)) return null;
        var chosen = parts.Select(static part => part!).ToArray();
        if (chosen.Sum(static file => file.Size) > MaximumModelBytes) return null;
        return new(IdOf(address), repository.Repository, repository.Repository.Split('/')[0], "", "", repository.Repository, commit, repository.Folder, family, chosen);
    }

    /// <summary>
    /// Gets whether a name is one path segment that stays where it is put: no separator, no parent folder, no
    /// character a file system or an address gives a meaning to.
    /// </summary>
    public static bool IsSafeSegment(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name is "." or ".." || name[0] == '.' || name[^1] is '.' or ' ') return false;
        foreach (var character in name)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.')) return false;
        }

        return true;
    }
}
