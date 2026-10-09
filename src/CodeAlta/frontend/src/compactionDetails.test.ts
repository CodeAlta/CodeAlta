import assert from "node:assert/strict";
import test from "node:test";
import { compactionDetailsMarkdown, splitCheckpointSummary } from "./compactionDetails";

const details = {
  schema: "codealta.localCompaction.v1", tokensBefore: 250000, tokensAfter: 61000, tokensRemoved: 189000, compressionRatio: 0.244,
  targetTokens: 68000, targetRatio: 0.25, targetMet: true, postCompactionInputRatio: 0.224, planningAttemptCount: 2,
  summarizedMessageCount: 156, keptMessageCount: 12, messagesAfter: 14, summaryCallCount: 1, chunkCount: 3,
  summaryPromptInputTokens: 48000, summaryMaxOutputTokens: 8000, summaryPromptIncludedMessageCount: 150, summaryPromptTotalMessageCount: 156,
  droppedMessageCount: 6, totalToolCallCount: 90, serializedToolCallCount: 80, collapsedToolCallCount: 10, totalToolResultCount: 90,
  serializedToolResultCount: 70, serializedToolResultExcerptCount: 40, omittedToolResultCount: 20, serializedToolResultCharacters: 12345,
  totalReasoningCount: 30, serializedReasoningCount: 25, omittedReasoningCount: 5, serializedReasoningCharacters: 4000,
  omittedAttachmentCount: 1, readFiles: ["a", "b"], modifiedFiles: ["c"], isSplitTurn: true, summaryMarkdown: "## Goal\nShip it.",
};

test("a local compaction is described like the terminal does", () => {
  const markdown = compactionDetailsMarkdown(details)!;
  assert.ok(markdown.startsWith("**Efficiency**\n- Context: 250,000 → 61,000 tokens, removed 189,000, ratio 24.4%"));
  assert.ok(markdown.includes("- Target: 68,000 tokens (25.0% of input limit), actual 22.4% of input limit, met, 2 planning attempts"));
  assert.ok(markdown.includes("- Messages: summarized 156, kept 12, after 14"));
  assert.ok(markdown.includes("- Summarizer: 1 call, 3 chunks, input ~48,000 tokens, output budget 8,000 tokens"));
  assert.ok(markdown.includes("- Messages serialized: 150/156 considered, 6 dropped as empty/unserializable"));
  assert.ok(markdown.includes("- Tool calls: 80/90 serialized, 10 repeated calls collapsed"));
  assert.ok(markdown.includes("- Tool outputs: 40/90 with excerpts, 70 result summaries, 20 omitted/truncated bulk outputs, 12,345 chars included"));
  assert.ok(markdown.includes("- Reasoning: 25/30 excerpts, 5 omitted, 4,000 chars included"));
  assert.ok(markdown.includes("- Attachments/files: 1 inline attachments omitted; 1 modified files and 2 read files tracked"));
  assert.ok(markdown.includes("**Special handling**\n- Compaction split an in-progress turn and retained a turn prefix."));
  assert.ok(markdown.endsWith("**Checkpoint summary**\n\n## Goal\nShip it."));
});

test("what the summary requests used is given beside the estimate, when the provider reports it", () => {
  assert.ok(!compactionDetailsMarkdown(details)!.includes("Summarizer usage"));
  const used = { ...details, summaryInputTokens: 21345, summaryCachedInputTokens: 0, summaryCacheWriteTokens: 0, summaryOutputTokens: 1234 };
  assert.ok(compactionDetailsMarkdown(used)!.includes("- Summarizer: 1 call, 3 chunks, input ~48,000 tokens, output budget 8,000 tokens\n"
    + "- Summarizer usage: input 21,345 tokens, output 1,234 tokens\n"));
  const billed = { ...used, summaryCachedInputTokens: 1000, summaryCacheWriteTokens: 20345, summaryCost: 0.420049, summaryCostUnit: "AI credits" };
  assert.ok(compactionDetailsMarkdown(billed)!.includes("- Summarizer usage: input 21,345 tokens (cache 1,000 · cache write 20,345), output 1,234 tokens, cost 0.42 AI credits\n"));
});

test("a missed target names its reason and missing counts read unknown", () => {
  const markdown = compactionDetailsMarkdown({ schema: "codealta.localCompaction.v1", tokensBefore: 1000, targetTokens: 500, targetMet: false, targetMissReason: "retained_suffix" })!;
  assert.ok(markdown.includes("- Context before: 1,000 tokens"));
  assert.ok(markdown.includes("- Target: 500 tokens, missed (retained suffix exceeded target)"));
  assert.ok(markdown.includes("- Messages: summarized unknown, kept unknown, after unknown"));
  assert.ok(!markdown.includes("Special handling") && !markdown.includes("Checkpoint summary"));
});

test("the history's shape is read too: list counts and the summary from the row text", () => {
  const split = splitCheckpointSummary("Threshold local compaction summarized 156 messages.\n\n**Checkpoint summary**\n\n## Goal\nShip it.\n");
  assert.deepEqual(split, { message: "Threshold local compaction summarized 156 messages.", summary: "## Goal\nShip it." });
  assert.deepEqual(splitCheckpointSummary("No summary here."), { message: "No summary here.", summary: null });
  assert.deepEqual(splitCheckpointSummary(null), { message: null, summary: null });
  const markdown = compactionDetailsMarkdown({ schema: "codealta.localCompaction.v1", readFilesCount: 7, modifiedFilesCount: 3 }, split.summary)!;
  assert.ok(markdown.includes("3 modified files and 7 read files tracked"));
  assert.ok(markdown.endsWith("**Checkpoint summary**\n\n## Goal\nShip it."));
});

test("details of another kind are not described", () => {
  assert.equal(compactionDetailsMarkdown(null), null);
  assert.equal(compactionDetailsMarkdown({ schema: "other" }), null);
  assert.equal(compactionDetailsMarkdown([details]), null);
  assert.equal(compactionDetailsMarkdown("text"), null);
});
