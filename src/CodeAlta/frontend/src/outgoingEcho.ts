// Image-bearing provider echoes append attachment descriptions to the original text.
// Only a known identical run can authorize that relaxed display-only comparison.
export function matchesOutgoingText(text: string | null | undefined, original: string, imageCount: number,
  expectedRun: string | null, observedRun: string | null | undefined): boolean {
  if (expectedRun && observedRun !== expectedRun) return false;
  if (text === original) return true;
  return imageCount > 0 && !!expectedRun && observedRun === expectedRun && typeof text === "string"
    && (!original.trim() || text.startsWith(`${original.trimEnd()}\n`));
}
