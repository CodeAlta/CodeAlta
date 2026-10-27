using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeAlta.Plugins.Abstractions;

[Plugin("instruction-path-normalizer", DisplayName = "Instruction Path Normalizer", Description = "Normalizes generated runtime-context paths before provider submission.")]
public sealed class InstructionPathNormalizerPlugin : PluginBase
{
    public override IEnumerable<PluginInstructionProcessorContribution> GetInstructionProcessors()
    {
        yield return new PluginInstructionProcessorContribution
        {
            Name = "normalize-runtime-context-paths",
            Target = new PluginInstructionProcessingTarget
            {
                Channels = PluginInstructionChannels.Developer,
                Stages = PluginInstructionProcessingStages.FinalBeforeProviderRequest,
            },
            Capabilities = PluginInstructionProcessorCapabilities.Read |
                PluginInstructionProcessorCapabilities.Replace |
                PluginInstructionProcessorCapabilities.ReportsChangeSummary,
            Handler = static (context, cancellationToken) => NormalizeRuntimeContextPathsAsync(context, cancellationToken),
        };
    }

    private static ValueTask<PluginInstructionProcessingResult> NormalizeRuntimeContextPathsAsync(
        PluginInstructionProcessingContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = context.Instructions.DeveloperInstructions;
        var normalized = NormalizeRuntimeContextSection(current);
        if (string.Equals(current, normalized, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(PluginInstructionProcessingResult.Continue);
        }

        return ValueTask.FromResult(PluginInstructionProcessingResult.Replace(
            developerInstructions: normalized,
            changeSummary: "Normalized path separators in the generated runtime context."));
    }

    // The paths are in the Runtime Context and in the Session Context (where the session works), which comes last.
    private static string? NormalizeRuntimeContextSection(string? developerInstructions)
        => NormalizeSection(NormalizeSection(developerInstructions, "# Runtime Context"), "# Session Context");

    private static string? NormalizeSection(string? developerInstructions, string heading)
    {
        if (string.IsNullOrEmpty(developerInstructions))
        {
            return developerInstructions;
        }

        var start = developerInstructions.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
        {
            return developerInstructions;
        }

        var nextHeading = developerInstructions.IndexOf("\n# ", start + heading.Length, StringComparison.Ordinal);
        var end = nextHeading < 0 ? developerInstructions.Length : nextHeading;
        var section = developerInstructions[start..end];
        var normalizedSection = section.Replace('\\', '/');
        return string.Equals(section, normalizedSection, StringComparison.Ordinal)
            ? developerInstructions
            : developerInstructions[..start] + normalizedSection + developerInstructions[end..];
    }
}
