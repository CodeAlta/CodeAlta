namespace CodeAlta.ModelServers.Store;

/// <summary>Whether a quantization fits a machine, before it is downloaded.</summary>
/// <param name="Fit"><c>gpu</c> (on its largest GPU), <c>gpus</c> (on its GPUs together), <c>partly</c> (part on the processor: slow) or <c>too_large</c>.</param>
/// <param name="Message">What it means, for people.</param>
/// <param name="Approximate">Whether it is an estimate (always, until the machine measured it).</param>
public sealed record FitResult(string Fit, string Message, bool Approximate);

/// <summary>
/// A first estimate of what a quantization needs: its weights, its vision projector and a margin of 1 GiB plus a tenth for
/// the context and the buffers, against the GPUs of the machine. The real measure is made on the machine once the model
/// is there (<c>--fit</c> chooses the context that fits).
/// </summary>
public static class FitEstimate
{
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>Estimates whether a quantization fits a machine.</summary>
    /// <param name="quant">The quantization.</param>
    /// <param name="hardware">The machine.</param>
    /// <returns>The estimate.</returns>
    public static FitResult For(QuantFiles quant, HardwareInventory hardware)
    {
        ArgumentNullException.ThrowIfNull(quant);
        ArgumentNullException.ThrowIfNull(hardware);
        var need = quant.TotalBytes + GiB + quant.ModelBytes / 10;
        var discrete = hardware.Gpus.Where(static gpu => gpu.MemoryBytes is > 0 && !gpu.Integrated).Select(static gpu => gpu.MemoryBytes!.Value).OrderDescending().ToList();
        // Apple Silicon and the processors with a large shared memory (Strix Halo) run on their integrated GPU.
        if (discrete.Count == 0 && hardware.Gpus.FirstOrDefault(static gpu => gpu.Integrated && gpu.Vendor is "apple" || gpu.MemoryBytes > 16 * GiB) is { MemoryBytes: { } shared })
            discrete.Add(hardware.Gpus.First(static gpu => gpu.Vendor == "apple") is { } apple ? shared * 3 / 4 : shared);
        var ram = hardware.MemoryBytes ?? 0;
        if (discrete.Count > 0 && need <= discrete[0])
            return new FitResult("gpu", $"Fits on one GPU ({Gb(need)} of {Gb(discrete[0])}).", true);
        if (discrete.Count > 1 && need <= discrete.Sum())
            return new FitResult("gpus", $"Fits on {discrete.Count} GPUs together ({Gb(need)} of {Gb(discrete.Sum())}).", true);
        if (need <= discrete.Sum() + ram * 3 / 4)
            return new FitResult("partly", discrete.Count == 0 ? $"Runs on the processor ({Gb(need)}): slow." : $"Partly on the processor ({Gb(need)}): slower.", true);
        return new FitResult("too_large", $"Too large for this machine ({Gb(need)}).", true);
    }

    private static string Gb(long bytes) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB");
}
