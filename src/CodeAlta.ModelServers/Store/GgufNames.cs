using System.Globalization;
using System.Text.RegularExpressions;
using CodeAlta.ModelServers.HuggingFace;

namespace CodeAlta.ModelServers.Store;

/// <summary>A quantization a repository offers: its main file (the first shard), its shards and the vision projector that goes with it.</summary>
/// <param name="Tag">Its tag, <c>UD-Q4_K_XL</c>.</param>
/// <param name="Files">Its files: the shards in order, then the vision projector when there is one.</param>
/// <param name="ModelBytes">The size of its shards.</param>
/// <param name="MmprojBytes">The size of its vision projector, 0 without one.</param>
/// <param name="Bits">The bits per weight its tag says (4 for Q4_K_M), 0 when not known.</param>
public sealed record QuantFiles(string Tag, IReadOnlyList<HubFile> Files, long ModelBytes, long MmprojBytes, int Bits)
{
    /// <summary>Gets the size of all its files.</summary>
    public long TotalBytes => ModelBytes + MmprojBytes;

    /// <summary>Gets whether it reads images.</summary>
    public bool Vision => MmprojBytes > 0;
}

/// <summary>
/// The names of GGUF files as llama.cpp reads them (<c>common/download.cpp</c>): a split file is
/// <c>name-00001-of-00003.gguf</c>, the tag is the last <c>-</c> or <c>.</c> part of the name (unsloth's <c>UD-</c> kept, as
/// <c>-hf repo:UD-Q4_K_XL</c> finds it), vision projectors have <c>mmproj</c> in their name, and draft models start with
/// <c>mtp-</c>, <c>dflash-</c>, <c>eagle3-</c> or <c>dspark-</c>.
/// </summary>
public static partial class GgufNames
{
    /// <summary>The parts of a GGUF file name.</summary>
    /// <param name="Prefix">The name without its extension and shard suffix.</param>
    /// <param name="Tag">Its tag in upper case, or empty.</param>
    /// <param name="Index">Its shard, from 1.</param>
    /// <param name="Count">How many shards.</param>
    public readonly record struct SplitInfo(string Prefix, string Tag, int Index, int Count);

    /// <summary>Reads a file name.</summary>
    /// <param name="path">The path of the file in its repository.</param>
    /// <returns>Its parts, or null for a file that is not GGUF.</returns>
    public static SplitInfo? Split(string path)
    {
        if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return null;
        var prefix = path[..^5];
        int index = 1, count = 1;
        var split = SplitPattern().Match(prefix);
        if (split.Success)
        {
            prefix = split.Groups[1].Value;
            index = int.Parse(split.Groups[2].Value, CultureInfo.InvariantCulture);
            count = int.Parse(split.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        var name = prefix[(prefix.LastIndexOf('/') + 1)..];
        var tag = TagPattern().Match(name) is { Success: true } match ? match.Groups[1].Value.ToUpperInvariant() : "";
        return new SplitInfo(prefix, tag, index, count);
    }

    /// <summary>Whether a file is a vision projector.</summary>
    /// <param name="path">Its path.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsMmproj(string path) => FileName(path).Contains("mmproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a GGUF file is a part beside a model (a projector, a draft model, an importance matrix) rather than a model.</summary>
    /// <param name="path">Its path.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsSidecar(string path)
    {
        var name = FileName(path);
        return IsMmproj(path) || name.Contains("imatrix", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("mtp-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("dflash-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("eagle3-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("dspark-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The bits per weight a tag says: 4 for <c>Q4_K_M</c>, 16 for <c>BF16</c>, 0 when it says none.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The bits.</returns>
    public static int Bits(string tag)
    {
        var digits = DigitsPattern().Match(tag.StartsWith("UD-", StringComparison.OrdinalIgnoreCase) ? tag[3..] : tag);
        return digits.Success && int.TryParse(digits.Value, CultureInfo.InvariantCulture, out var bits) ? bits : 0;
    }

    /// <summary>Lists the quantizations of a repository from its files, the smallest first.</summary>
    /// <param name="files">Its files.</param>
    /// <returns>Its quantizations.</returns>
    public static IReadOnlyList<QuantFiles> Quantizations(IReadOnlyList<HubFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var result = new List<QuantFiles>();
        foreach (var first in files.Where(static file => !IsSidecar(file.Path)).Select(static file => (File: file, Info: Split(file.Path)))
                     .Where(static entry => entry.Info is { Index: 1, Tag.Length: > 0 }))
        {
            var info = first.Info!.Value;
            var shards = files.Select(static file => (File: file, Info: Split(file.Path)))
                .Where(entry => entry.Info is { } other && other.Prefix == info.Prefix && other.Count == info.Count)
                .OrderBy(static entry => entry.Info!.Value.Index).Select(static entry => entry.File).ToList();
            if (shards.Count != info.Count) continue; // a split file with shards missing
            if (result.Any(existing => existing.Tag == info.Tag)) continue;
            var mmproj = FindSibling(files, first.File.Path, info.Tag);
            var all = mmproj is null ? shards : [.. shards, mmproj];
            result.Add(new QuantFiles(info.Tag, all, shards.Sum(static file => Math.Max(0, file.Size)), mmproj?.Size ?? 0, Bits(info.Tag)));
        }

        return result.OrderBy(static quant => quant.ModelBytes).ToList();
    }

    /// <summary>Finds the quantization of a tag, as <c>-hf repo:TAG</c> does: the tag as a whole part of the name.</summary>
    /// <param name="files">The files of the repository.</param>
    /// <param name="tag">The tag, or null for llama.cpp's default (Q4_K_M, then Q8_0, then the first).</param>
    /// <returns>The quantization, or null.</returns>
    public static QuantFiles? Find(IReadOnlyList<HubFile> files, string? tag)
    {
        var quants = Quantizations(files);
        if (tag is null) return quants.FirstOrDefault(static q => q.Tag == "Q4_K_M") ?? quants.FirstOrDefault(static q => q.Tag == "Q8_0") ?? quants.FirstOrDefault();
        var exact = quants.FirstOrDefault(quant => string.Equals(quant.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var pattern = new Regex($"(^|[-./]){Regex.Escape(tag)}[.-]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return quants.FirstOrDefault(quant => pattern.IsMatch(FileName(quant.Files[0].Path)));
    }

    // The projector of a model is in its folder or a folder above it; the one of the same tag wins, then the closest bits.
    private static HubFile? FindSibling(IReadOnlyList<HubFile> files, string model, string tag)
    {
        var folder = Folder(model);
        var bits = Bits(tag);
        return files.Where(file => IsMmproj(file.Path) && file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                                   && (folder.StartsWith(Folder(file.Path), StringComparison.Ordinal)))
            .OrderByDescending(file => Folder(file.Path).Length)
            .ThenByDescending(file => FileName(file.Path).Contains(tag, StringComparison.OrdinalIgnoreCase))
            .ThenBy(file => Math.Abs((Split(file.Path)?.Tag is { Length: > 0 } t ? Bits(t) : 16) - Math.Max(bits, 16)))
            .FirstOrDefault();
    }

    private static string Folder(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..(slash + 1)] : "";

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    [GeneratedRegex("^(.+)-([0-9]{5})-of-([0-9]{5})$")]
    private static partial Regex SplitPattern();

    [GeneratedRegex(@"[-.]((?:UD-)?[A-Z0-9_]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TagPattern();

    [GeneratedRegex("[0-9]+")]
    private static partial Regex DigitsPattern();
}
