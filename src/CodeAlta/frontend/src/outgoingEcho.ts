/**
 * Whether a user message of the timeline is the prompt of an echo. The host records the identity of the Send with its
 * message, and that identity decides: the recorded text is not what was typed when the prompt names project files (they
 * become links, followed by a line per file) or when a plugin adds to it. A message without an identity (a record of an
 * older version, another path) is compared by its text.
 */
export function matchesOutgoing(echo: Readonly<{ clientRequestId: string; text: string; imageCount: number; runId: string | null }>,
  row: Readonly<{ clientRequestId?: string | null; text: string | null | undefined; runId: string | null | undefined }>): boolean {
  if (row.clientRequestId) return row.clientRequestId === echo.clientRequestId;
  return matchesOutgoingText(row.text, echo.text, echo.imageCount, echo.runId, row.runId);
}

// Image-bearing provider echoes append attachment descriptions to the original text.
// Only a known identical run can authorize that relaxed display-only comparison.
export function matchesOutgoingText(text: string | null | undefined, original: string, imageCount: number,
  expectedRun: string | null, observedRun: string | null | undefined): boolean {
  if (expectedRun && observedRun !== expectedRun) return false;
  if (text === original) return true;
  return imageCount > 0 && !!expectedRun && observedRun === expectedRun && typeof text === "string"
    && (!original.trim() || text.startsWith(`${original.trimEnd()}\n`));
}
