using System.Globalization;

namespace CodeAlta.ModelServers.Store;

/// <summary>Whether a quantization fits a machine, before it is downloaded.</summary>
/// <param name="Fit"><c>gpu</c> (on its largest GPU), <c>gpus</c> (on its GPUs together), <c>partly</c> (part on the processor: slow) or <c>too_large</c>.</param>
/// <param name="Message">What it means, for people.</param>
/// <param name="Approximate">Whether it is an estimate (always, until the machine measured it).</param>
/// <param name="Context">The largest context that fits on the GPUs, in tokens; null when not estimated.</param>
public sealed record FitResult(string Fit, string Message, bool Approximate, int? Context = null);

/// <summary>
/// A first estimate of what a quantization needs, against the GPUs of the machine: its weights, its vision projector, its
/// buffers, and its KV cache for the context. With the header of the model (<see cref="GgufShape"/>), the cache is the one
/// of its architecture (sliding windows, hybrid layers, latent attention) and the estimate says the largest context that
/// fits; without it, a tenth of the weights stands for the cache. The real measure is made on the machine once the model is
/// there (<c>llama-fit-params</c>).
/// </summary>
public static class FitEstimate
{
    /// <summary>The context below which a model is of little use to an agent, in tokens.</summary>
    public const int UsefulContext = 16_384;

    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>Estimates whether a quantization fits a machine, from its size alone.</summary>
    /// <param name="quant">The quantization.</param>
    /// <param name="hardware">The machine.</param>
    /// <returns>The estimate.</returns>
    public static FitResult For(QuantFiles quant, HardwareInventory hardware) => For(quant, hardware, null);

    /// <summary>Estimates whether a quantization fits a machine.</summary>
    /// <param name="quant">The quantization.</param>
    /// <param name="hardware">The machine.</param>
    /// <param name="shape">The shape of the model, from its header; null for an estimate from its size.</param>
    /// <returns>The estimate.</returns>
    public static FitResult For(QuantFiles quant, HardwareInventory hardware, GgufShape? shape)
    {
        ArgumentNullException.ThrowIfNull(quant);
        ArgumentNullException.ThrowIfNull(hardware);
        var gpus = GpuMemory(hardware);
        var ram = hardware.MemoryBytes ?? 0;
        if (shape is null || shape.TrainedContext <= 0)
        {
            var need = quant.TotalBytes + GiB + quant.ModelBytes / 10;
            return Verdict(need, gpus, ram, null, null);
        }

        // Weights, the buffers of the computation (the logits of a batch of 512 tokens, the activations), a margin, and the cache.
        var fixedBytes = quant.TotalBytes + 512L * 4 * Math.Max(shape.Vocabulary, 32_000) + GiB / 2;
        var useful = Math.Min(shape.TrainedContext, UsefulContext);
        var needUseful = fixedBytes + shape.KvBytes(useful);
        var largest = gpus.Count > 0 ? gpus[0] : 0;
        var room = needUseful <= largest ? largest : gpus.Sum();
        var context = room > 0 ? LargestContext(shape, room - fixedBytes) : 0;
        return Verdict(needUseful, gpus, ram, context > 0 ? context : null, shape);
    }

    // The largest context, a multiple of 1024 tokens up to the trained one, whose cache fits in the bytes given.
    private static int LargestContext(GgufShape shape, long bytes)
    {
        if (bytes <= 0) return 0;
        int low = 0, high = shape.TrainedContext / 1024;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (shape.KvBytes(middle * 1024) <= bytes) low = middle;
            else high = middle - 1;
        }

        return low * 1024;
    }

    private static FitResult Verdict(long need, List<long> gpus, long ram, int? context, GgufShape? shape)
    {
        var rough = shape?.Known == false ? " The layout of its architecture is not known here: the estimate is rough." : "";
        var tokens = context is { } value ? $" with up to {value.ToString("N0", CultureInfo.InvariantCulture)} tokens of context" : "";
        if (gpus.Count > 0 && need <= gpus[0])
            return new FitResult("gpu", $"Fits on one GPU ({Gb(need)} of {Gb(gpus[0])}){tokens}.{rough}", true, context);
        if (gpus.Count > 1 && need <= gpus.Sum())
            return new FitResult("gpus", $"Fits on {gpus.Count} GPUs together ({Gb(need)} of {Gb(gpus.Sum())}){tokens}.{rough}", true, context);
        if (need <= gpus.Sum() + ram * 3 / 4)
            return new FitResult("partly", (gpus.Count == 0 ? $"Runs on the processor ({Gb(need)}): slow." : $"Partly on the processor ({Gb(need)}): slower.") + rough, true);
        return new FitResult("too_large", $"Too large for this machine ({Gb(need)}).{rough}", true);
    }

    // The memory of the GPUs that run a model, the largest first. Apple Silicon and the processors with a large shared memory
    // (Strix Halo) run on their integrated GPU.
    private static List<long> GpuMemory(HardwareInventory hardware)
    {
        var discrete = hardware.Gpus.Where(static gpu => gpu.MemoryBytes is > 0 && !gpu.Integrated).Select(static gpu => gpu.MemoryBytes!.Value).OrderDescending().ToList();
        if (discrete.Count == 0 && hardware.Gpus.FirstOrDefault(static gpu => gpu.Integrated && (gpu.Vendor == "apple" || gpu.MemoryBytes > 16 * GiB)) is { MemoryBytes: { } shared } integrated)
            discrete.Add(integrated.Vendor == "apple" ? shared * 3 / 4 : shared);
        return discrete;
    }

    private static string Gb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB");
}
