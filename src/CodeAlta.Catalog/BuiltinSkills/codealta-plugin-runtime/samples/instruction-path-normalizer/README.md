# Instruction path normalizer sample

Demonstrates `GetInstructionProcessors()` for trusted final system/developer instruction transforms. The sample inspects the composed developer instructions after CodeAlta has added generated runtime context and replaces backslashes with forward slashes only inside the `# Runtime Context` and `# Session Context` sections.

Copy this folder to `~/.alta/plugins/instruction-path-normalizer/` or `<project>/.alta/plugins/instruction-path-normalizer/` to try it. When enabled, prompt manifest audit metadata records the plugin contribution and the high-level change summary without storing pre-transform instruction text.

This extension point runs in trusted in-process plugin code; it is not a sandbox or security boundary. Keep real transformations narrow, provider/model-gated when appropriate, and avoid putting secret values in `ChangeSummary` or metadata.
