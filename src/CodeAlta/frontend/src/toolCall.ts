import { parseUnifiedDiff, type DiffLine } from "./changes/unifiedDiff";
import { highlightCode } from "./codeHighlight";
import { fileLanguage } from "./monaco/fileLanguage";
import type { TimelineItem } from "./timeline";

/** How far a tool call is, as its tile and its details say it. */
export type ToolState = "pending" | "running" | "completed" | "failed" | "canceled";

/** The state the phase of a record stands for. A phase that is not known reads as pending. */
export function toolState(phase: string | undefined): ToolState {
  switch (phase) {
    case "completed": case "deselected": return "completed";
    case "failed": return "failed";
    case "canceled": return "canceled";
    case "started": case "progressed": case "selected": return "running";
    default: return "pending";
  }
}

/** A text of a call, and whether the page holds only a part of it. */
export type ToolText = Readonly<{ text: string; partial: boolean }>;

/** What is known of a tool call: from its timeline row at once, then from its whole record. */
export type ToolCall = Readonly<{
  name: string; kind: string; state: ToolState;
  command: string | null; workingDirectory: string | null; exitCode: number | null; error: string | null;
  /** The arguments as the provider gave them: the text of a JSON value, or a plain string. */
  arguments: ToolText | null; output: ToolText | null; diff: ToolText | null;
  readFiles: readonly string[]; modifiedFiles: readonly string[];
}>;

/** The arguments of a call as an object, when they are the text of one. */
export type ToolArguments = Readonly<Record<string, unknown>>;

/** Reads the arguments of a call: null when the text is cut, is no JSON or is not an object. */
export function parseArguments(value: ToolText | null): ToolArguments | null {
  if (!value || value.partial) return null;
  try {
    const parsed: unknown = JSON.parse(value.text);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as ToolArguments : null;
  } catch { return null; }
}

const text = (value: unknown): string | null => typeof value === "string" && value.length > 0 ? value : null;

/** The call a timeline row describes, within what the row holds: its texts can be cut. */
export function toolCallFromItem(item: TimelineItem): ToolCall {
  const field = (...paths: string[]): ToolText | null => {
    for (const path of paths) {
      const found = item.toolFields?.find(candidate => candidate.path === path);
      if (found) return { text: found.text, partial: found.truncated };
    }
    return null;
  };
  const given = field("arguments", "input");
  const parsed = parseArguments(given);
  const state = toolState(item.toolPhase);
  return {
    name: item.title, kind: item.toolCall?.kind ?? "ToolCall", state,
    // The row previews the first line of a command: the arguments have all of it.
    command: !item.summaryIsCode ? null : text(parsed?.command) ?? field("command")?.text ?? item.summary,
    workingDirectory: text(parsed?.workdir) ?? text(parsed?.cwd), exitCode: item.toolExitCode ?? null,
    error: state === "failed" ? item.markdown : null,
    arguments: given,
    output: field("content", "aggregatedOutput", "result.content", "error.message", "output.body", "result.detailedContent", "output", "result"),
    diff: field("diff"), readFiles: [], modifiedFiles: [],
  };
}

/** The record of a call as the host serves it (`toolCalls.read`), with its texts read to their end or to the page's limit. */
export type ToolCallRecord = Readonly<{ kind: string; phase: string; name: string | null; message: string | null; command: string | null;
  workingDirectory: string | null; exitCode: number | null; error: string | null;
  arguments: ToolText | null; output: ToolText | null; diff: ToolText | null; readFiles: readonly string[]; modifiedFiles: readonly string[] }>;

/** The call its whole record describes. The name and the state stay those of the row, which follows the live view. */
export function toolCallFromRecord(record: ToolCallRecord, row: ToolCall): ToolCall {
  return { name: row.name, kind: record.kind, state: row.state, command: record.command ?? row.command,
    workingDirectory: record.workingDirectory ?? row.workingDirectory, exitCode: record.exitCode ?? row.exitCode,
    error: row.state === "failed" ? record.error ?? row.error : null,
    arguments: record.arguments ?? row.arguments, output: record.output ?? row.output, diff: record.diff ?? row.diff,
    readFiles: record.readFiles, modifiedFiles: record.modifiedFiles };
}

/** What kind of view shows a call best. */
export type ToolFamily = "shell" | "read" | "edit" | "generic";

const editTools = ["write_file", "replace_in_file", "apply_patch", "edit", "write", "multiedit", "create_file", "str_replace_editor", "str_replace_based_edit_tool", "edit_file"];
const readTools = ["read_file", "read", "view", "cat"];

/**
 * The view of a call: a call that left a diff or that edits files shows its changes, one that runs a command
 * shows a terminal, one that read a file shows the file, and any other shows its arguments and its result.
 */
export function toolFamily(call: ToolCall): ToolFamily {
  const name = call.name.toLowerCase();
  if (call.diff?.text.trim() || editTools.includes(name)) return "edit";
  if (name === "alta") return "generic";
  if (call.command !== null || call.kind === "CommandExecution") return "shell";
  if (readTools.includes(name) && call.output && parseNumberedLines(call.output.text)) return "read";
  return "generic";
}

/** The result of the `shell_command` tool as its text states it. */
export type ShellResult = Readonly<{ exitCode: number; workingDirectory: string; stdout: string; stderr: string }>;

/**
 * Reads `exit_code`, `working_directory`, `stdout` and `stderr` from the result of the `shell_command` tool, or
 * returns null when the text does not have that form. The last `stderr:` line starts the standard error: a
 * command that prints such a line itself is not told apart.
 */
export function parseShellResult(value: string): ShellResult | null {
  if (!value.startsWith("exit_code: ")) return null;
  const normalized = value.replaceAll("\r\n", "\n");
  const header = /^exit_code: (-?\d{1,10})\nworking_directory: ([^\n]*)\nstdout:\n/.exec(normalized);
  if (!header) return null;
  const body = header[0].length;
  const split = normalized.lastIndexOf("\nstderr:\n");
  if (split < body - 1) return null;
  const stdout = normalized.slice(body, Math.max(body, split)), stderr = normalized.slice(split + 9);
  return { exitCode: Number(header[1]), workingDirectory: header[2], stdout: stdout === "(empty)" ? "" : stdout, stderr: stderr === "(empty)" ? "" : stderr };
}

/** The lines of a file as a tool numbered them: the number of the first one and the lines without their numbers. */
export type NumberedLines = Readonly<{ start: number; lines: readonly string[] }>;

/**
 * Reads lines written as `   12: text` with consecutive numbers (the result of `read_file`), or returns null
 * when a line has another form. A last line cut by the page's limit is dropped.
 */
export function parseNumberedLines(value: string, partial = false): NumberedLines | null {
  const rows = value.replaceAll("\r\n", "\n").split("\n");
  if (rows.at(-1) === "") rows.pop();
  if (partial) rows.pop();
  if (!rows.length) return null;
  const lines: string[] = [];
  let start = 0;
  for (const row of rows) {
    const match = /^ *(\d{1,9}): ?(.*)$/.exec(row);
    if (!match) return null;
    const number = Number(match[1]);
    if (!lines.length) start = number;
    else if (number !== start + lines.length) return null;
    lines.push(match[2]);
  }
  return { start, lines };
}

/** The changes of one file: the rows of its diff, and the lines it adds and removes. */
export type ChangedFile = Readonly<{ path: string; operation: "add" | "delete" | "update"; movedTo: string | null;
  rows: readonly DiffLine[]; added: number; removed: number }>;

function changedFile(path: string, operation: ChangedFile["operation"], movedTo: string | null, rows: DiffLine[]): ChangedFile {
  let added = 0, removed = 0;
  for (const row of rows) if (row.kind === "added") added++; else if (row.kind === "removed") removed++;
  return { path, operation, movedTo, rows, added, removed };
}

/**
 * The files of a unified diff, each with its own rows. A file that only adds lines from line 1 of nothing is
 * new, and one that only removes lines to nothing is deleted. A diff that names no file is one file without a
 * name.
 */
export function diffFiles(diff: string): ChangedFile[] {
  const files: ChangedFile[] = [];
  let path: string | null = null, rows: DiffLine[] = [];
  const close = () => {
    if (path === null && !rows.length) return;
    const lines = rows.filter(row => row.kind === "added" || row.kind === "removed" || row.kind === "context");
    const operation = lines.length && lines.every(row => row.kind === "added") && lines[0].newLine === 1 ? "add"
      : lines.length && lines.every(row => row.kind === "removed") && lines[0].oldLine === 1 ? "delete" : "update";
    files.push(changedFile(path ?? "", operation, null, rows));
  };
  for (const row of parseUnifiedDiff(diff)) {
    if (row.kind === "file") { close(); path = row.text; rows = []; }
    else rows.push(row);
  }
  close();
  return files;
}

/**
 * The files of a patch in the `apply_patch` format (`*** Begin Patch`, `*** Update File:`, `@@`, lines that start
 * with a space, `-` or `+`). The format has no line numbers: the rows have none. Returns null for a text that is
 * no such patch.
 */
export function patchFiles(patch: string): ChangedFile[] | null {
  const lines = patch.replaceAll("\r\n", "\n").split("\n");
  if (lines[0]?.trim() !== "*** Begin Patch") return null;
  const files: ChangedFile[] = [];
  let current: { path: string; operation: ChangedFile["operation"]; movedTo: string | null; rows: DiffLine[] } | null = null;
  const close = () => { if (current) files.push(changedFile(current.path, current.operation, current.movedTo, current.rows)); current = null; };
  const row = (kind: DiffLine["kind"], content: string): DiffLine => ({ kind, text: content, oldLine: null, newLine: null });
  for (const line of lines.slice(1)) {
    const header = /^\*\*\* (Add|Delete|Update) File: (.+)$/.exec(line);
    if (header) {
      close();
      current = { path: header[2].trim(), operation: header[1] === "Add" ? "add" : header[1] === "Delete" ? "delete" : "update", movedTo: null, rows: [] };
      continue;
    }
    if (line.trim() === "*** End Patch") break;
    if (!current) continue;
    if (line.startsWith("*** Move to: ")) current.movedTo = line.slice(13).trim();
    else if (line.startsWith("*** End of File")) continue;
    else if (line.startsWith("@@")) current.rows.push(row("hunk", line.slice(2).trim()));
    else if (line.startsWith("+")) current.rows.push(row("added", line.slice(1)));
    else if (line.startsWith("-")) current.rows.push(row("removed", line.slice(1)));
    else current.rows.push(row("context", line.startsWith(" ") ? line.slice(1) : line));
  }
  close();
  return files;
}

/** The change `replace_in_file` asks for, before its record has a diff: the old text removed, the new one added. */
export function replacementFile(path: string, oldText: string, newText: string): ChangedFile {
  const rows = (value: string, kind: "added" | "removed"): DiffLine[] => value.replaceAll("\r\n", "\n").split("\n")
    .map(line => ({ kind, text: line, oldLine: null, newLine: null }));
  return changedFile(path, "update", null, [...rows(oldText, "removed"), ...rows(newText, "added")]);
}

/** The files a call changes: from the diff its record has, or from what its arguments ask for while it has none. */
export function changedFiles(call: ToolCall, given: ToolArguments | null): ChangedFile[] {
  if (call.diff?.text.trim()) return diffFiles(call.diff.text);
  const path = text(given?.path) ?? text(given?.file_path) ?? "";
  const input = text(given?.input) ?? text(given?.patch);
  if (input) return patchFiles(input) ?? [];
  if (typeof given?.old_string === "string" && typeof given.new_string === "string") return [replacementFile(path, given.old_string, given.new_string)];
  if (typeof given?.content === "string") {
    return [changedFile(path, "add", null, given.content.replaceAll("\r\n", "\n").replace(/\n$/, "").split("\n")
      .map((line, index) => ({ kind: "added" as const, text: line, oldLine: null, newLine: index + 1 })))];
  }
  return [];
}

// highlight.js names where they differ from the names of the editor.
const highlightNames: Readonly<Record<string, string>> = { shell: "bash", bat: "dos", html: "xml", toml: "ini", "objective-c": "objectivec", vb: "vbnet", mdx: "markdown", razor: "xml" };

/** The highlight.js language of a file path; "plaintext" when none is known. */
export function highlightLanguage(path: string): string {
  const language = fileLanguage(path);
  return highlightNames[language] ?? language;
}

/**
 * Highlights lines that follow each other in a file and returns the HTML of each, made only of `span` elements
 * and escaped text; null when the language is unknown or the text is too long. A span left open at the end of a
 * line (a comment, a string over several lines) is closed there and opened again on the next line.
 */
export function highlightLines(lines: readonly string[], language: string): string[] | null {
  const html = highlightCode(lines.join("\n"), language);
  if (html === null) return null;
  const result: string[] = [];
  const open: string[] = [];
  for (const line of html.split("\n")) {
    const prefix = open.join("");
    for (const tag of line.matchAll(/<span class="[^"]*">|<\/span>/g)) {
      if (tag[0] === "</span>") open.pop(); else open.push(tag[0]);
    }
    result.push(prefix + line + "</span>".repeat(open.length));
  }
  return result.length === lines.length ? result : null;
}

/**
 * The HTML of the rows of a file's diff, highlighted as their language: the lines of each side of a hunk are
 * highlighted together, so that a construct over several lines keeps its colors. A row without HTML is shown as text.
 */
export function highlightDiffRows(rows: readonly DiffLine[], language: string): (string | null)[] {
  const result: (string | null)[] = rows.map(() => null);
  if (language === "plaintext") return result;
  let start = 0;
  const flush = (end: number) => {
    for (const side of ["removed", "added"] as const) {
      const indexes: number[] = [];
      for (let index = start; index < end; index++) if (rows[index].kind === side || rows[index].kind === "context") indexes.push(index);
      if (!indexes.length) continue;
      const html = highlightLines(indexes.map(index => rows[index].text), language);
      if (html) indexes.forEach((index, position) => { if (rows[index].kind === side || result[index] === null) result[index] = html[position]; });
    }
  };
  rows.forEach((row, index) => { if (row.kind === "hunk") { flush(index); start = index + 1; } });
  flush(rows.length);
  return result;
}

/** One argument of a call as its details show it: on a line with its name, or in a block under it. */
export type ArgumentEntry = Readonly<{ name: string; value: string; block: boolean; language: string | null }>;

/**
 * The arguments of a call for display: those that are null are left out, a short value stays on the line of
 * its name, and a long text, a text of several lines, an array or an object gets a block.
 */
export function argumentEntries(given: ToolArguments, skip: readonly string[] = []): ArgumentEntry[] {
  const entries: ArgumentEntry[] = [];
  for (const [name, value] of Object.entries(given)) {
    if (value === null || value === undefined || skip.includes(name)) continue;
    if (typeof value === "string") {
      if (!value) continue;
      const block = value.length > 240 || value.includes("\n");
      entries.push({ name, value, block, language: block && /^(function|script|code|javascript|js)$/i.test(name) ? "javascript" : null });
    } else if (typeof value === "object") {
      if (Array.isArray(value) && value.length === 0) continue;
      const inline = Array.isArray(value) && value.every(item => typeof item !== "object") && JSON.stringify(value).length <= 96;
      entries.push({ name, value: inline ? (value as unknown[]).map(item => String(item)).join(", ") : JSON.stringify(value, null, 2), block: !inline, language: inline ? null : "json" });
    } else entries.push({ name, value: String(value), block: false, language: null });
  }
  return entries;
}

/** One entry of a folder as `list_dir` lists it. */
export type DirectoryEntry = Readonly<{ name: string; directory: boolean }>;

/** The entries of the result of `list_dir` (`[dir]  name`, `[file] name`), or null when a line has another form. */
export function parseDirectoryListing(value: string): DirectoryEntry[] | null {
  const lines = value.split(/\r?\n/).filter(line => line.trim());
  if (lines.length === 1 && lines[0].trim() === "(empty directory)") return [];
  const entries: DirectoryEntry[] = [];
  for (const line of lines) {
    const match = /^\[(dir|file)\] {1,2}(.+)$/.exec(line);
    if (!match) return null;
    entries.push({ name: match[2], directory: match[1] === "dir" });
  }
  return entries.length ? entries : null;
}

/** The lines `grep` found in one file. */
export type SearchFile = Readonly<{ path: string; matches: ReadonlyArray<Readonly<{ line: number; text: string }>> }>;

/** The matches of the result of `grep` (`path:line: text`) by file, in order, or null when a line has another form. */
export function parseSearchMatches(value: string): SearchFile[] | null {
  const lines = value.split(/\r?\n/).filter(line => line.trim());
  if (lines.length === 1 && lines[0].trim() === "(no matches)") return [];
  const files: { path: string; matches: { line: number; text: string }[] }[] = [];
  for (const line of lines) {
    // The path can hold colons (a drive): the first `:number: ` after it ends it.
    const match = /^(.+?):(\d{1,9}): ?(.*)$/.exec(line);
    if (!match) return null;
    const last = files.at(-1);
    const found = { line: Number(match[2]), text: match[3] };
    if (last?.path === match[1]) last.matches.push(found); else files.push({ path: match[1], matches: [found] });
  }
  return files.length ? files : null;
}

/**
 * Where a search pattern matches in a line, as [start, end) ranges. The pattern is a .NET regular expression:
 * one the page cannot read, or that matches nothing, gives no range.
 */
export function matchRanges(line: string, pattern: string, caseSensitive: boolean): [number, number][] {
  let expression: RegExp;
  try { expression = new RegExp(pattern, caseSensitive ? "g" : "gi"); }
  catch { return []; }
  const ranges: [number, number][] = [];
  for (let match = expression.exec(line); match && ranges.length < 64; match = expression.exec(line)) {
    if (match[0].length === 0) { expression.lastIndex++; continue; }
    ranges.push([match.index, match.index + match[0].length]);
  }
  return ranges;
}

/** A result made of JSON values, one per line (the `alta` commands write theirs so). */
export type JsonRecords = ReadonlyArray<Readonly<{ type: string | null; value: unknown; text: string }>>;

/** The records of a text that holds one JSON object per line, or null when a line is something else. */
export function parseJsonRecords(value: string): JsonRecords | null {
  const lines = value.split(/\r?\n/).filter(line => line.trim());
  if (!lines.length || lines.length > 2000) return null;
  const records: { type: string | null; value: unknown; text: string }[] = [];
  for (const line of lines) {
    if (!line.startsWith("{")) return null;
    try {
      const parsed: unknown = JSON.parse(line);
      if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null;
      records.push({ type: text((parsed as Record<string, unknown>).type), value: parsed, text: JSON.stringify(parsed, null, 2) });
    } catch { return null; }
  }
  return records;
}

/**
 * How a result is best shown: as the records of JSON lines, as one formatted JSON value, as Markdown when it has
 * fenced code blocks (what MCP tools write), or as it is.
 */
export type OutputShape = Readonly<{ kind: "records"; records: JsonRecords } | { kind: "json"; text: string }
  | { kind: "markdown"; text: string } | { kind: "text"; text: string }>;

export function outputShape(value: ToolText): OutputShape {
  if (!value.partial) {
    // Two fence lines at least: a text with a code block is written as Markdown.
    if ((value.text.match(/^```/gm)?.length ?? 0) >= 2) return { kind: "markdown", text: value.text };
    const trimmed = value.text.trim();
    if (trimmed.startsWith("{") || trimmed.startsWith("[")) {
      try { return { kind: "json", text: JSON.stringify(JSON.parse(trimmed), null, 2) }; }
      catch { /* Several values, or no JSON: read as lines next. */ }
      const records = parseJsonRecords(value.text);
      if (records) return { kind: "records", records };
    }
  }
  return { kind: "text", text: value.text };
}

/** The envelope the `alta` commands write first: its exit code and the time the command took. */
export function altaEnvelope(records: JsonRecords): Readonly<{ exitCode: number | null; durationMs: number | null }> | null {
  const first = records[0]?.value as Record<string, unknown> | undefined;
  if (!first || first.type !== "alta.result") return null;
  return { exitCode: typeof first.exitCode === "number" ? first.exitCode : null, durationMs: typeof first.durationMs === "number" ? first.durationMs : null };
}

/** A duration for a label: `0.4 s`, `12 s`, `3 min 05 s`, `1 h 02 min`. */
export function formatDuration(milliseconds: number): string {
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return "";
  if (milliseconds < 1000) return `${Math.round(milliseconds)} ms`;
  const seconds = milliseconds / 1000;
  if (seconds < 10) return `${seconds.toFixed(1)} s`;
  if (seconds < 60) return `${Math.round(seconds)} s`;
  const minutes = Math.floor(seconds / 60), rest = Math.floor(seconds % 60);
  if (minutes < 60) return `${minutes} min ${String(rest).padStart(2, "0")} s`;
  return `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, "0")} min`;
}

/** A size for a label: `512 B`, `1.4 KB`, `3.2 MB`. */
export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** The lines of a text, not counting the empty ones at its end. */
export function countLines(value: string): number {
  const body = value.replaceAll("\r\n", "\n").replace(/\n+$/, "");
  return body ? body.split("\n").length : 0;
}

/** The time between the first record of a call and the record that ended it, when the window has both. */
export function callDuration(startedAt: string | null | undefined, endedAt: string | null | undefined): number | null {
  if (!startedAt || !endedAt) return null;
  const duration = Date.parse(endedAt) - Date.parse(startedAt);
  return Number.isFinite(duration) && duration >= 0 ? duration : null;
}
