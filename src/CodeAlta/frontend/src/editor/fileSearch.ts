import type { ProjectFileSearchEvent } from "#neoastra";
import type { MessageKey } from "../localization";

/** What a search through the files of a project looks for. */
export type SearchOptions = Readonly<{ query: string; regex: boolean; matchCase: boolean; wholeWord: boolean; include: string; exclude: string }>;
export const emptySearchOptions: SearchOptions = Object.freeze({ query: "", regex: false, matchCase: false, wholeWord: false, include: "", exclude: "" });
export type SearchMatch = Readonly<{ line: number; column: number; length: number; text: string; start: number; shown: number }>;
export type SearchFile = Readonly<{ path: string; matches: readonly SearchMatch[]; truncated: boolean }>;
export type SearchResults = Readonly<{
  /** The search these are the results of. */
  key: string;
  files: readonly SearchFile[];
  matches: number;
  state: "searching" | "done" | "failed";
  /** Why the search did not complete, when `state` is "failed". */
  status: string;
  /** The search stopped at its limits: there is more than is listed. */
  truncated: boolean;
}>;

/** Shortest text searched for as it is typed. */
export const searchMinimumLength = 2;
export const searchKey = (options: SearchOptions) => JSON.stringify([options.query, options.regex, options.matchCase, options.wholeWord, options.include.trim(), options.exclude.trim()]);
/** Whether there is something to search for. */
export const searchable = (options: SearchOptions) => options.query.length >= searchMinimumLength && !/[\r\n]/u.test(options.query);
export const startSearch = (options: SearchOptions): SearchResults => ({ key: searchKey(options), files: [], matches: 0, state: "searching", status: "ok", truncated: false });

const number = (value: unknown): value is number => typeof value === "number" && Number.isInteger(value) && value >= 0;
function match(value: unknown): SearchMatch | null {
  if (!value || typeof value !== "object") return null;
  const data = value as Record<string, unknown>;
  return number(data.line) && data.line > 0 && number(data.column) && data.column > 0 && number(data.length) && typeof data.text === "string"
    && number(data.start) && number(data.shown) && data.start + data.shown <= data.text.length
    ? { line: data.line, column: data.column, length: data.length, text: data.text, start: data.start, shown: data.shown } : null;
}

/** Applies one event of the host to the results it belongs to. Anything malformed ends the search as failed. */
export function searchEvent(results: SearchResults, event: ProjectFileSearchEvent): SearchResults {
  if (results.state !== "searching") return results;
  const failed = (status: string): SearchResults => ({ ...results, state: "failed", status });
  if (event.kind === "done") return event.status === "ok" ? { ...results, state: "done", truncated: event.truncated } : failed(event.status);
  if (event.kind !== "file" || event.status !== "ok" || typeof event.path !== "string" || !event.path || !Array.isArray(event.matches)) return failed("invalid_response");
  const matches = event.matches.map(match);
  if (!matches.length || matches.some(value => value === null) || results.files.some(file => file.path === event.path)) return failed("invalid_response");
  return { ...results, files: [...results.files, { path: event.path, matches: matches as SearchMatch[], truncated: event.truncated }], matches: results.matches + matches.length };
}

/** The message for a search that did not complete. */
export function searchFailure(status: string): MessageKey {
  switch (status) {
    case "invalid_pattern": return "The regular expression is not valid.";
    case "invalid_glob": return "A file pattern is not valid.";
    case "timeout": return "The search took too long and was stopped. Try a simpler expression.";
    case "unknown_project": case "project_unavailable": case "archived_project": return "The project folder is unavailable.";
    case "unavailable": return "File editing requires an owned host.";
    default: return "The search could not be completed.";
  }
}

export type SearchRow = Readonly<
  | { kind: "file"; key: string; file: SearchFile; collapsed: boolean }
  | { kind: "match"; key: string; path: string; match: SearchMatch }>;

/** The rows shown: each file, followed by its matches unless it is folded. */
export function searchRows(results: SearchResults | null, collapsed: ReadonlySet<string>): SearchRow[] {
  const rows: SearchRow[] = [];
  for (const file of results?.files ?? []) {
    const folded = collapsed.has(file.path);
    rows.push({ kind: "file", key: file.path, file, collapsed: folded });
    if (!folded) file.matches.forEach((value, index) => rows.push({ kind: "match", key: `${file.path}\n${index}`, path: file.path, match: value }));
  }
  return rows;
}

/** Longest text shown before a match: the list is narrow, and the match has to be seen in it. */
export const matchLeadLength = 18;
const wordCharacter = /[\p{L}\p{N}_]/u;

// The end of what precedes a match: cut at the start of a word when one starts there, and never inside a character.
function matchLead(text: string, length: number): string {
  if (text.length <= length) return text;
  let cut = text.length - length;
  const unit = text.charCodeAt(cut);
  if (unit >= 0xdc00 && unit <= 0xdfff) cut++;
  if (wordCharacter.test(text[cut - 1]) && wordCharacter.test(text[cut] ?? "")) {
    const next = text.slice(cut).search(/[^\p{L}\p{N}_]/u);
    if (next >= 0) cut += next;
  }
  return `…${text.slice(cut).trimStart()}`;
}

/**
 * The text of a match as three parts: before the match, the match, after it. Only the end of a long start is
 * kept, so that the match shows in a narrow list.
 */
export function matchParts(value: SearchMatch, lead = matchLeadLength): readonly [string, string, string] {
  return [matchLead(value.text.slice(0, value.start), lead), value.text.slice(value.start, value.start + value.shown), value.text.slice(value.start + value.shown)];
}
