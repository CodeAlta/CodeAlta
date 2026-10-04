const schema = "codealta.localCompaction.v1";

type Details = Readonly<Record<string, unknown>>;
const integer = (details: Details, name: string): number | null => {
  const value = details[name];
  return typeof value === "number" && Number.isFinite(value) ? Math.trunc(value) : null;
};
const ratio = (details: Details, name: string): number | null => {
  const value = details[name];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
};
const flag = (details: Details, name: string): boolean | null => typeof details[name] === "boolean" ? details[name] as boolean : null;
const text = (details: Details, name: string): string | null => typeof details[name] === "string" && (details[name] as string).trim() ? details[name] as string : null;
// A list arrives as its length (`{name}Count`) from the history, or whole from a live event.
const count = (details: Details, name: string): number => integer(details, `${name}Count`) ?? (Array.isArray(details[name]) ? (details[name] as unknown[]).length : 0);
const number = (value: number | null): string => value === null ? "unknown" : value.toLocaleString("en-US");
const percent = (value: number): string => `${(value * 100).toFixed(1)}%`;
const plural = (value: number | null, one: string, many: string): string => `${number(value)} ${value === 1 ? one : many}`;

function missReason(reason: string): string {
  switch (reason) {
    case "fixed_prompt": return "fixed prompt exceeded target";
    case "oversized_anchor_reduced": return "latest user anchor required reduction";
    case "latest_user_anchor": return "latest user anchor exceeded target";
    case "summary_size": return "checkpoint summary exceeded target";
    case "retained_suffix": return "retained suffix exceeded target";
    case "input_fit_only": return "accepted to fit the input limit";
    default: return reason.replaceAll("_", " ");
  }
}

/** The heading the host puts between a compaction's message and its checkpoint summary in the row's text. */
export const checkpointSummaryHeading = "**Checkpoint summary**";

/** Splits the text of a compaction row into its message and its checkpoint summary (null when it has none). */
export function splitCheckpointSummary(text: string | null): { message: string | null; summary: string | null } {
  const at = text?.indexOf(`\n\n${checkpointSummaryHeading}\n\n`) ?? -1;
  return at < 0 || text === null ? { message: text, summary: null }
    : { message: text.slice(0, at), summary: text.slice(at + checkpointSummaryHeading.length + 4).trim() || null };
}

/**
 * What a local compaction did, as the terminal shows it: how much context it removed against its target, what
 * was summarized and kept, what fed the summarizer, and the checkpoint summary it produced. Null when the
 * details are not those of a local compaction (another provider's compaction carries only its message).
 */
export function compactionDetailsMarkdown(details: unknown, checkpoint: string | null = null): string | null {
  if (!details || typeof details !== "object" || Array.isArray(details) || (details as Details).schema !== schema) return null;
  const value = details as Details;
  const lines: string[] = ["**Efficiency**"];
  const before = integer(value, "tokensBefore"), after = integer(value, "tokensAfter"), removed = integer(value, "tokensRemoved");
  const compression = ratio(value, "compressionRatio");
  if (before !== null && after !== null)
    lines.push(`- Context: ${number(before)} → ${number(after)} tokens${removed === null ? "" : `, removed ${number(removed)}`}${compression === null ? "" : `, ratio ${percent(compression)}`}`);
  else if (before !== null) lines.push(`- Context before: ${number(before)} tokens`);

  const targetTokens = integer(value, "targetTokens"), targetRatio = ratio(value, "targetRatio"), targetMet = flag(value, "targetMet");
  if (targetTokens !== null || targetRatio !== null || targetMet !== null) {
    let target = `- Target: ${targetTokens === null ? "unknown tokens" : `${number(targetTokens)} tokens`}`;
    if (targetRatio !== null) target += ` (${percent(targetRatio)} of input limit)`;
    const actual = ratio(value, "postCompactionInputRatio");
    if (actual !== null) target += `, actual ${percent(actual)} of input limit`;
    if (targetMet !== null) target += targetMet ? ", met" : ", missed";
    const reason = text(value, "targetMissReason");
    if (targetMet === false && reason && reason.toLowerCase() !== "none") target += ` (${missReason(reason)})`;
    const attempts = integer(value, "planningAttemptCount");
    if (attempts !== null && attempts > 1) target += `, ${attempts} planning attempts`;
    lines.push(target);
  }
  lines.push(`- Messages: summarized ${number(integer(value, "summarizedMessageCount"))}, kept ${number(integer(value, "keptMessageCount"))}, after ${number(integer(value, "messagesAfter"))}`);
  lines.push(`- Summarizer: ${plural(integer(value, "summaryCallCount"), "call", "calls")}, ${plural(integer(value, "chunkCount"), "chunk", "chunks")}, `
    + `input ~${number(integer(value, "summaryPromptInputTokens"))} tokens, output budget ${number(integer(value, "summaryMaxOutputTokens"))} tokens`);

  lines.push("", "**What fed the summarizer**");
  const dropped = integer(value, "droppedMessageCount"), collapsed = integer(value, "collapsedToolCallCount");
  lines.push(`- Messages serialized: ${number(integer(value, "summaryPromptIncludedMessageCount"))}/${number(integer(value, "summaryPromptTotalMessageCount"))} considered`
    + (dropped !== null && dropped > 0 ? `, ${dropped} dropped as empty/unserializable` : ""));
  lines.push(`- Tool calls: ${number(integer(value, "serializedToolCallCount"))}/${number(integer(value, "totalToolCallCount"))} serialized`
    + (collapsed !== null && collapsed > 0 ? `, ${collapsed} repeated calls collapsed` : ""));
  lines.push(`- Tool outputs: ${number(integer(value, "serializedToolResultExcerptCount"))}/${number(integer(value, "totalToolResultCount"))} with excerpts, `
    + `${number(integer(value, "serializedToolResultCount"))} result summaries, ${number(integer(value, "omittedToolResultCount"))} omitted/truncated bulk outputs, `
    + `${number(integer(value, "serializedToolResultCharacters"))} chars included`);
  lines.push(`- Reasoning: ${number(integer(value, "serializedReasoningCount"))}/${number(integer(value, "totalReasoningCount"))} excerpts, `
    + `${number(integer(value, "omittedReasoningCount"))} omitted, ${number(integer(value, "serializedReasoningCharacters"))} chars included`);
  lines.push(`- Attachments/files: ${number(integer(value, "omittedAttachmentCount"))} inline attachments omitted; `
    + `${count(value, "modifiedFiles")} modified files and ${count(value, "readFiles")} read files tracked`);

  const split = flag(value, "isSplitTurn") === true, reduced = flag(value, "oversizedAnchorReduced") === true;
  if (split || reduced) {
    lines.push("", "**Special handling**");
    if (split) lines.push("- Compaction split an in-progress turn and retained a turn prefix.");
    if (reduced) lines.push("- The oversized latest user message was reduced before summarization.");
  }
  const summary = checkpoint ?? text(value, "summaryMarkdown");
  if (summary) lines.push("", "**Checkpoint summary**", "", summary.trim());
  return lines.join("\n");
}
