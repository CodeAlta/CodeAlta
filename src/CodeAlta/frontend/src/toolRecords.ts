import type { HistoryEntry } from "./timeline";

export type ToolRecord = Readonly<{ source: string; raw: string; name: string | null; provenance: string;
  partial: boolean; fields: readonly Readonly<{ path: string; text: string }>[] }>;
const object = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);

// Only explicit ToolCall activity Details fields understood by ToolCallEventInterpreter.
// No message/command parsing, content association, phase-to-outcome inference or live input.
export function projectToolRecord(entry: HistoryEntry): ToolRecord | undefined {
  if (entry.eventType !== "activity" || entry.kind?.toLowerCase() !== "toolcall"
    || !entry.details || entry.details.length > 8192 || entry.detailsTruncated
    || (entry.text?.length ?? 0) > 32768) return;
  const identities = [entry.offset, entry.sessionId, entry.providerId, entry.runId, entry.activityId,
    entry.parentActivityId, entry.contentId, entry.interactionId, entry.name, entry.phase, entry.timestamp];
  if (identities.some(value => value !== null && (typeof value !== "string" || value.length > 256))) return;
  let value: unknown;
  try {
    // JSON.parse supplies syntax validation; bounded lexical inspection rejects
    // duplicate decoded keys before they can acquire last-key-wins authority.
    const tokens = entry.details.match(/"(?:\\.|[^"\\])*"|[{}\[\]:,]/g) ?? [];
    if (tokens.length > 2048) return;
    const stack: Array<Set<string> | null> = [];
    for (let index = 0; index < tokens.length; index++) {
      const token = tokens[index];
      if (token === "{" || token === "[") { stack.push(token === "{" ? new Set() : null); if (stack.length > 32) return; }
      else if (token === "}" || token === "]") stack.pop();
      else if (token.startsWith('"') && tokens[index + 1] === ":") {
        const key: string = JSON.parse(token); const keys = stack.at(-1);
        if (!keys || keys.has(key)) return;
        keys.add(key);
      }
    }
    value = JSON.parse(entry.details);
  } catch { return; }
  if (!object(value)) return;
  const fields: Array<{ path: string; text: string }> = [];
  if (Object.hasOwn(value, "arguments")) {
    if (typeof value.arguments !== "string" && !object(value.arguments) && !Array.isArray(value.arguments)) return;
    fields.push({ path: "arguments", text: typeof value.arguments === "string" ? value.arguments : JSON.stringify(value.arguments) });
  }
  for (const [parent, key] of [["result", "content"], ["result", "detailedContent"], ["output", "body"], ["error", "message"]]) {
    if (!Object.hasOwn(value, parent)) continue;
    const container = value[parent];
    if (!object(container)) return;
    if (!Object.hasOwn(container, key)) continue;
    if (typeof container[key] !== "string") return;
    fields.push({ path: `${parent}.${key}`, text: container[key] });
  }
  if (!fields.length) return;
  return { source: JSON.stringify(entry), raw: entry.details, name: entry.name,
    provenance: JSON.stringify({ eventType: entry.eventType, kind: entry.kind, phase: entry.phase,
      providerId: entry.providerId, sessionId: entry.sessionId, runId: entry.runId, activityId: entry.activityId,
      parentActivityId: entry.parentActivityId, offset: entry.offset, timestamp: entry.timestamp }, null, 2),
    partial: entry.bodyOmitted || entry.textTruncated, fields };
}
