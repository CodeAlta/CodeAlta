import type { HistoryResponse, SessionDisplayText, SessionDisplayToolActivity, SessionDisplayView } from "#neoastra";
import { buildTimelineItems, type TimelineItem } from "./timeline";

export type ReconciledRow =
  | { source: "history"; key: string; item: TimelineItem }
  | { source: "liveText"; key: string; row: SessionDisplayText }
  | { source: "liveTool"; key: string; row: SessionDisplayToolActivity };

// A tool call has one key from its request to its end, whichever record or live row shows it: its tile and
// its details stay mounted while it runs.
function rowKey(item: TimelineItem): string {
  return item.toolCall ? `tool:${toolKey(item.toolCall.providerId, item.toolCall.runId, item.toolCall.activityId)}` : `history:${item.key}`;
}

const phaseRank = (phase: string | undefined) => !phase || phase === "requested" ? 0
  : ["completed", "failed", "canceled", "deselected"].includes(phase) ? 2 : 1;
const livePhases = new WeakMap<TimelineItem, Map<string, TimelineItem>>();

// The live view learns of a phase before the journal is read again: the row says the later of the two.
function withLivePhase(item: TimelineItem, phase: string): TimelineItem {
  const live = phase.toLowerCase();
  if (phaseRank(live) <= phaseRank(item.toolPhase)) return item;
  let known = livePhases.get(item);
  if (!known) livePhases.set(item, known = new Map());
  let shown = known.get(live);
  if (!shown) known.set(live, shown = { ...item, toolPhase: live });
  return shown;
}

// What the records of a window say whatever the live view holds: its rows, and the text and the call each one
// belongs to. A running session changes its live view many times a second and its window a few times: this
// is worked out once per window, so that a live update costs the same however long the window is. A window
// is never changed in place: the history gives a new array when it reads records (see mergeHistoryPage).
type Journal = Readonly<{
  rows: readonly Readonly<{ item: TimelineItem; text: string | null; call: string | null }>[];
  /** The providers that wrote each text, by its key. */
  providers: ReadonlyMap<string, ReadonlySet<string>>;
  /** The pieces of text the journal holds, with the row that shows each. */
  deltas: readonly Readonly<{ offset: string; text: string; item: TimelineItem | undefined }>[];
  /** The texts the journal holds whole. */
  completed: ReadonlySet<string>;
}>;
const journals = new WeakMap<HistoryResponse["entries"], Journal>();
function journalOf(entries: HistoryResponse["entries"]): Journal {
  let journal = journals.get(entries);
  if (journal) return journal;
  const historical = buildTimelineItems(entries);
  const providers = new Map<string, Set<string>>();
  const completed = new Set<string>();
  // Looked up by journal offset: a long window must not be searched once per record.
  const itemAt = new Map(historical.map(item => [item.key, item]));
  const entryAt = new Map(entries.map(entry => [entry.offset, entry]));
  const deltas: { offset: string; text: string; item: TimelineItem | undefined }[] = [];
  for (const entry of entries) {
    if (!entry.runId || !entry.contentId) continue;
    if (entry.kind && ["contentDelta", "contentCompleted"].includes(entry.eventType)) {
      const key = textKey(entry.runId, entry.contentId, entry.kind);
      const values = providers.get(key) ?? new Set<string>();
      values.add(entry.providerId);
      providers.set(key, values);
    }
    if (entry.eventType === "contentCompleted") completed.add(textKey(entry.runId, entry.contentId, entry.kind ?? ""));
    else if (entry.eventType === "contentDelta")
      deltas.push({ offset: entry.offset, text: textKey(entry.runId, entry.contentId, entry.kind ?? ""), item: itemAt.get(entry.offset) });
  }
  const rows = historical.map(item => {
    const entry = entryAt.get(item.key);
    return { item, text: entry?.runId && entry.contentId ? textKey(entry.runId, entry.contentId, entry.kind ?? "") : null,
      call: item.toolCall?.runId ? toolKey(item.toolCall.providerId, item.toolCall.runId, item.toolCall.activityId) : null };
  });
  journals.set(entries, journal = { rows, providers, deltas, completed });
  return journal;
}

// Source timestamps and first-publication sequences keep streaming rows in place.
// Only a run-scoped, unambiguous match can replace a journal row.
export function reconcileTimeline(entries: HistoryResponse["entries"], live: SessionDisplayView | null): ReconciledRow[] {
  const journal = journalOf(entries);
  if (!live) return orderTimelineRows(journal.rows.map(({ item }) => ({ source: "history", key: rowKey(item), item })));
  const textMatches = new Map<string, SessionDisplayText>();
  for (const row of live.text) {
    if (!row.runId || !live.configuration?.providerId) continue;
    const key = textKey(row.runId, row.contentId, row.kind), writers = journal.providers.get(key);
    if (writers?.size === 1 && writers.has(live.configuration.providerId)) textMatches.set(key, row);
  }
  const completed = new Set<string>();
  for (const key of textMatches.keys()) if (journal.completed.has(key)) completed.add(key);
  const activityMatches = new Map(live.toolActivities.filter(row => row.runId).map(row => [toolKey(row.providerId, row.runId!, row.activityId), row]));
  const hidden = new Set<string>();
  const coveredText = new Set<string>();
  if (textMatches.size) for (const delta of journal.deltas) {
    const row = textMatches.get(delta.text);
    if (row && delta.item && !completed.has(delta.text)) {
      const persisted = delta.item.markdown ?? "";
      if (row.isComplete && !row.isTruncated || row.text.startsWith(persisted)) hidden.add(delta.offset);
      else if (persisted.startsWith(row.text)) coveredText.add(delta.text);
    }
  }
  // A call the journal shows keeps its row, which says more than the live one: the command, its output.
  const journaled = new Set<string>();
  const result: ReconciledRow[] = [];
  for (const { item, text, call } of journal.rows) {
    if (hidden.has(item.key)) continue;
    if (call) journaled.add(call);
    const reported = call ? activityMatches.get(call) : undefined;
    const key = text && textMatches.has(text) && (completed.has(text) || coveredText.has(text)) ? `text:${text}` : rowKey(item);
    result.push({ source: "history", key, item: reported ? withLivePhase(item, reported.phase) : item });
  }
  for (const row of live.toolActivities) {
    if (row.runId && journaled.has(toolKey(row.providerId, row.runId, row.activityId))) continue;
    result.push({ source: "liveTool", key: `tool:${toolKey(row.providerId, row.runId, row.activityId)}`, row });
  }
  for (const row of live.text) {
    if (row.kind.toLowerCase().startsWith("reasoning") && !row.text.trim()) continue;
    if (row.runId && (completed.has(textKey(row.runId, row.contentId, row.kind)) || coveredText.has(textKey(row.runId, row.contentId, row.kind)))) continue;
    result.push({ source: "liveText", key: `text:${textKey(row.runId, row.contentId, row.kind)}`, row });
  }
  return orderTimelineRows(result);
}

// Parsing a timestamp is the cost of ordering a long timeline; an item or a live row keeps its own.
const times = new WeakMap<object, number>();
export function orderTimelineRows(result: ReconciledRow[]): ReconciledRow[] {
  const timestamp = (row: ReconciledRow) => {
    const subject = row.source === "history" ? row.item : row.row;
    let time = times.get(subject);
    if (time === undefined) times.set(subject, time = Date.parse(row.source === "history" ? row.item.timestamp : row.row.timestamp ?? ""));
    return time;
  };
  result.sort((a, b) => {
    const time = timestamp(a) - timestamp(b);
    if (Number.isFinite(time) && time !== 0) return time;
    if (a.source !== "history" && b.source !== "history" && a.row.sequence && b.row.sequence) {
      const left = BigInt(a.row.sequence), right = BigInt(b.row.sequence);
      return left < right ? -1 : left > right ? 1 : 0;
    }
    return 0;
  });
  // Preparation records precede the provider's persisted user echo. Present that
  // contiguous setup block after its prompt, without falsifying source timestamps
  // or moving notices across assistant/tool/other turn boundaries.
  const setup = (row: ReconciledRow) => row.source === "history" &&
    (row.item.category === "prompt" || row.item.eventType === "sessionUpdate" && row.item.icon === "model");
  for (let index = 0; index < result.length; index++) {
    const row = result[index];
    if (!(row.source === "history" ? row.item.category === "user" : row.source === "liveText" && row.row.kind.toLowerCase() === "user")) continue;
    let start = index;
    while (start > 0 && setup(result[start - 1])) start--;
    if (start !== index) { result.splice(index, 1); result.splice(start, 0, row); }
  }
  return result;
}

function textKey(runId: string | null, contentId: string, kind: string): string {
  return JSON.stringify([runId, contentId, kind.toLowerCase()]);
}

function toolKey(providerId: string, runId: string | null, activityId: string): string {
  return JSON.stringify([providerId, runId, activityId]);
}
