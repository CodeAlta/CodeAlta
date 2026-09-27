import type { HistoryEntry } from "./timeline";

export type FileChangeRow = Readonly<{ index: number; path: string; kind: string | null; diff: string | null;
  counts: Readonly<{ added: number; removed: number }> | null }>;
export type FileChanges = Readonly<{ source: string; rows: readonly FileChangeRow[]; partial: boolean }>;
const object = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
const text = (value: unknown, limit: number): value is string => typeof value === "string" && value.length > 0 && value.length <= limit;

// The existing host projects at most 8 Ki UTF-16 units of raw Details. Never repair
// truncated JSON or recursively discover paths in arbitrary provider payloads.
export function projectFileChanges(entry: HistoryEntry): FileChanges | undefined {
  if (!(entry.eventType === "activity" && entry.kind?.toLowerCase() === "filechange")
    && !(entry.eventType === "sessionUpdate" && entry.kind?.toLowerCase() === "diffupdated")) return;
  const empty: FileChanges = { source: "", rows: [], partial: true };
  if (!entry.details || entry.details.length > 8192 || entry.detailsTruncated) return empty;
  let value: unknown;
  try { value = JSON.parse(entry.details); } catch { return empty; }
  if (!object(value)) return empty;
  // These two explicit shapes match FileChangePresenter's Codex/workspace readers.
  const candidates = Array.isArray(value.changes) ? value.changes : typeof value.path === "string" ? [value]
    : typeof value.diff === "string" ? splitSuppliedDiff(value.diff) : null;
  if (!candidates) return empty; // Aggregate-only diffs remain raw; do not guess file boundaries.
  let partial = entry.bodyOmitted || entry.textTruncated || candidates.length > 32;
  const rows: FileChangeRow[] = [];
  for (let index = 0; index < Math.min(candidates.length, 32); index++) {
    const candidate: unknown = candidates[index];
    if (!object(candidate) || !text(candidate.path, 512)) { partial = true; continue; }
    const kind = object(candidate.kind) ? candidate.kind.type : candidate.operation;
    const diff = candidate.diff;
    if (candidate.kind != null && !object(candidate.kind)) partial = true;
    if ((kind != null && !text(kind, 64)) || (diff != null && !text(diff, 4096))) partial = true;
    rows.push({ index, path: candidate.path, kind: text(kind, 64) ? kind : null,
      diff: text(diff, 4096) ? diff : null, counts: text(diff, 4096) ? countSuppliedHunks(diff) : null });
  }
  return { source: rows.length ? JSON.stringify(entry) : "", rows, partial };
}

// Only complete, bounded unified file sections are projected. Paths remain display data;
// quoted/ambiguous headers and malformed hunks never become guessed file counts.
function splitSuppliedDiff(diff: string): Record<string, unknown>[] | null {
  if (diff.length > 8192) return null;
  const sections = diff.split(/(?=^diff --git )/m).filter(section => section.trim());
  if (!sections.length || sections.length > 32) return null;
  const rows: Record<string, unknown>[] = [];
  for (const section of sections) {
    const lines = section.split(/\r?\n/);
    if (!lines[0].startsWith("diff --git ")) return null;
    const before = lines.find(line => line.startsWith("--- "))?.slice(4);
    const after = lines.find(line => line.startsWith("+++ "))?.slice(4);
    if (!before || !after || !(before === "/dev/null" || before.startsWith("a/"))
      || !(after === "/dev/null" || after.startsWith("b/"))) return null;
    const path = after === "/dev/null" ? before.slice(2) : after.slice(2);
    if (!path || /[\t\r\n]/.test(path)) return null;
    const hunk = lines.findIndex(line => line.startsWith("@@ "));
    rows.push({ path, operation: before === "/dev/null" ? "create" : after === "/dev/null" ? "delete" : "update",
      diff: hunk < 0 ? undefined : lines.slice(hunk).join("\n") });
  }
  return rows;
}

// Counts describe only this supplied per-file hunk text, never whole-file/run totals.
// Linear bounded work: <=4096 units, <=512 lines, <=128-unit hunk headers. A malformed,
// incomplete, multi-file or unsupported diff gets unknown counts, never a guessed zero.
function countSuppliedHunks(diff: string): FileChangeRow["counts"] {
  const lines = diff.split("\n");
  if (lines.at(-1) === "") lines.pop();
  if (lines.length > 512) return null;
  let old = 0, next = 0, added = 0, removed = 0, hunks = 0;
  let preamble: "none" | "git" | "index" | "old" | "new" = "none";
  let oldEnd = 0, nextEnd = 0, markerAllowed = false;
  let lastPrefix = "", oldNoNewline = false, nextNoNewline = false;
  for (const raw of lines) {
    const line = raw.endsWith("\r") ? raw.slice(0, -1) : raw;
    if (line.startsWith("@@")) {
      if (old || next || oldNoNewline || nextNoNewline || line.length > 128 || (!hunks && preamble !== "none" && preamble !== "new")) return null;
      const match = /^@@ -([0-9]{1,7})(?:,([0-9]{1,4}))? \+([0-9]{1,7})(?:,([0-9]{1,4}))? @@(?: .*)?$/.exec(line);
      if (!match) return null;
      const oldStart = Number(match[1]), nextStart = Number(match[3]);
      old = Number(match[2] ?? 1); next = Number(match[4] ?? 1);
      if (old > 512 || next > 512 || (!old && !next) || (old && !oldStart) || (next && !nextStart)) return null;
      // Empty ranges name the preceding line; normalize to half-open boundaries.
      const oldBoundary = oldStart + (old === 0 ? 1 : 0), nextBoundary = nextStart + (next === 0 ? 1 : 0);
      if (hunks && (oldBoundary < oldEnd || nextBoundary < nextEnd)) return null;
      oldEnd = oldBoundary + old; nextEnd = nextBoundary + next;
      markerAllowed = false; hunks++; continue;
    }
    if (!hunks) {
      // Deliberately exclude quoted/extended metadata rather than partially parsing it.
      if (preamble === "none" && /^diff --git a\/[^\s]+ b\/[^\s]+$/.test(line)) { preamble = "git"; continue; }
      if (preamble === "git" && /^index [0-9a-f]{1,64}\.\.[0-9a-f]{1,64}(?: [0-7]{6})?$/.test(line)) { preamble = "index"; continue; }
      if (["none", "git", "index"].includes(preamble) && line.startsWith("--- ") && line.length > 4) { preamble = "old"; continue; }
      if (preamble === "old" && line.startsWith("+++ ") && line.length > 4) { preamble = "new"; continue; }
      return null;
    }
    if (line === "\\ No newline at end of file") {
      if (!markerAllowed) return null;
      if (lastPrefix !== "+") { if (old !== 0) return null; oldNoNewline = true; }
      if (lastPrefix !== "-") { if (next !== 0) return null; nextNoNewline = true; }
      markerAllowed = false; continue;
    }
    if ((oldNoNewline && !line.startsWith("+")) || (nextNoNewline && !line.startsWith("-"))) return null;
    if (line.startsWith("+")) { added++; next--; }
    else if (line.startsWith("-")) { removed++; old--; }
    else if (line.startsWith(" ")) { old--; next--; }
    else return null;
    if (old < 0 || next < 0) return null;
    markerAllowed = true; lastPrefix = line[0];
  }
  return hunks && old === 0 && next === 0 ? { added, removed } : null;
}
