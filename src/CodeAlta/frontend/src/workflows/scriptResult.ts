/**
 * The text of the result of a script of Code Mode, as the host writes it for the model (CodeModeResultFormatter): a
 * header ("Script completed · 2.0 s · 3 calls"), then what the run wrote (an error, its output, its profile), what it
 * showed the user (`present()`, in a block after its line), then the result last, after a line `Result:`. A result
 * longer than its budget keeps its start and its end, and a line `Full result: <path> (<lines> lines, <n> characters; …)`
 * before it names the file that keeps the whole of it.
 */
export type ScriptResult = Readonly<{
  /** The header of the text: "Script completed · 36m50s · 4 calls, 4 at once". */
  status: string;
  phase: "completed" | "failed" | "canceled";
  /** What the run wrote before its result, as Markdown; empty when it wrote nothing. */
  notes: string;
  /** What the run showed the user (`present()`), as Markdown, as the model was told it: null when it presented nothing. */
  presented: string | null;
  /** The result, as Markdown, or null when the script returned nothing. */
  result: string | null;
  /** The file that keeps the whole result, when the text has only its start and its end. */
  fullResult: Readonly<{ path: string; characters: string }> | null;
}>;

/** The prompt that gives a session the result of a script that ended in the background: "Script run-7f3c2a (Title) ended:". */
export type WorkflowResult = ScriptResult & Readonly<{ runId: string; title: string | null }>;

const header = /^Script (completed|stopped|failed)\b/;
/** The line before the parts a script presented (CodeModeResultFormatter.PresentedHeading). */
export const presentedHeading = "Presented to the user (they saw it on the card of the run; do not repeat it):";
const presentedFence = /^(`{3,})markdown$/;
// "(52,341 characters; …)" before, "(930 lines, 52,341 characters; …)" or "(1,204 lines of indented JSON, … characters; …)" now.
const fullResultLine = /^Full result: (.+) \((?:[\d.,\s  ]+ lines?(?: of indented JSON)?, )?([\d.,\s  ]+) characters?[^)]*\)$/;
const fence = /^ {0,3}(`{3,}|~{3,})/;

/** Reads the text of the result of a script, or returns null for another text. */
export function parseScriptResult(text: string | null | undefined): ScriptResult | null {
  const lines = text?.replace(/\r\n/g, "\n").split("\n");
  const match = lines && header.exec(lines[0]);
  if (!lines || !match) return null;
  const phase = match[1] === "completed" ? "completed" : match[1] === "stopped" ? "canceled" : "failed";
  // The line of the result is the first one outside a block of code: the output is in one, and so are the parts the run
  // presented, which are read apart.
  let open: string | null = null, resultLine = -1;
  let presented: string | null = null, presentedFrom = -1, presentedTo = -1;
  for (let index = 1; index < lines.length; index++) {
    const line = lines[index];
    const opening = open === null && line === presentedHeading ? presentedFence.exec(lines[index + 1] ?? "")?.[1] : undefined;
    if (opening) {
      const end = lines.indexOf(opening, index + 2);
      if (end > 0) {
        presented = lines.slice(index + 2, end).join("\n");
        presentedFrom = index; presentedTo = end;
        index = end;
        continue;
      }
    }
    const marker = fence.exec(line)?.[1];
    if (marker) {
      if (open === null) open = marker;
      else if (marker[0] === open[0] && marker.length >= open.length && line.trim() === marker) open = null;
      continue;
    }
    if (open === null && line === "Result:") { resultLine = index; break; }
  }
  const noted = (from: number, to: number) => lines.slice(from, to).filter((_, offset) => from + offset < presentedFrom || from + offset > presentedTo);
  if (resultLine < 0) return { status: lines[0], phase, notes: noted(1, lines.length).join("\n").trim(), presented, result: null, fullResult: null };
  const full = fullResultLine.exec(lines[resultLine - 1] ?? "");
  const notes = noted(1, full ? resultLine - 1 : resultLine);
  const rest = lines.slice(resultLine + 1);
  // A text written before the result came last: the result was JSON, and what the run wrote followed it.
  const older = olderResult(rest);
  return {
    status: lines[0], phase,
    notes: [...notes, ...(older?.after ?? [])].join("\n").trim(),
    presented,
    result: older ? older.result : prettyJson(rest.join("\n").trimEnd()),
    fullResult: full ? { path: full[1], characters: full[2] } : null,
  };
}

/** Reads the prompt of a script that ended in the background, or returns null for another prompt. */
export function parseWorkflowResult(text: string | null | undefined): WorkflowResult | null {
  const match = text && /^Script (run-[\w-]+)(?: \((.*)\))? ended:\r?\n/.exec(text);
  if (!match) return null;
  const result = parseScriptResult(text.slice(match[0].length));
  return result && { ...result, runId: match[1], title: match[2] || null };
}

/** The result as plain text, as the model reads it: an object without the block of JSON it is shown in. */
export function plainResult(result: string): string {
  return /^```json\n([\s\S]*)\n```$/.exec(result)?.[1] ?? result;
}

// An object the result gives as a block of JSON is shown indented.
function prettyJson(result: string): string {
  const block = /^```json\n([\s\S]*)\n```$/.exec(result);
  if (!block) return result;
  try { return "```json\n" + JSON.stringify(JSON.parse(block[1]), null, 2) + "\n```"; }
  catch { return result; }
}

// The result as the host wrote it before: the JSON of the value on its line (or its start and its end around a line
// that says what was left out), then the output and the profile. A text is shown as itself, an object indented.
function olderResult(rest: readonly string[]): Readonly<{ result: string; after: readonly string[] }> | null {
  const first = rest[0] ?? "";
  if (!/^["{[]/.test(first)) return null;
  const whole = decode(first);
  if (whole !== null) return { result: whole, after: rest.slice(1) };
  const cut = /^… ([\d.,\s  ]+) characters left out …$/.exec(rest[1] ?? "");
  if (!cut || rest.length < 3) return null;
  // Only a text can be read back from its two pieces; each loses the escape it was cut in, if any.
  if (!first.startsWith('"')) return null;
  const head = piece(first, skip => first.slice(0, first.length - skip) + '"');
  // An end cut just after the backslash of an escape starts with the rest of it, such as `u0060`.
  const end = rest[2].replace(/^u[0-9a-fA-F]{4}/, "");
  const tail = piece(end, skip => '"' + end.slice(skip));
  if (head === null || tail === null) return null;
  return { result: `${head}\n\n… ${cut[1]} characters left out …\n\n${tail}`, after: rest.slice(3) };
}

// A JSON string as its text, another value indented in a block.
function decode(json: string): string | null {
  try {
    const value: unknown = JSON.parse(json);
    return typeof value === "string" ? value : "```json\n" + JSON.stringify(value, null, 2) + "\n```";
  } catch { return null; }
}

function piece(text: string, quoted: (skip: number) => string): string | null {
  for (let skip = 0; skip <= 6 && skip < text.length; skip++) {
    try { const value: unknown = JSON.parse(quoted(skip)); if (typeof value === "string") return value; } catch { /* Cut further. */ }
  }
  return null;
}
