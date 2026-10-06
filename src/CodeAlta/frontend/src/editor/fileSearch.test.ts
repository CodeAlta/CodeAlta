import assert from "node:assert/strict";
import test from "node:test";
import type { ProjectFileSearchEvent } from "#neoastra";
import { emptySearchOptions, matchParts, searchable, searchEvent, searchFailure, searchKey, searchRows, startSearch, type SearchOptions } from "./fileSearch";

const options = (change: Partial<SearchOptions> = {}): SearchOptions => ({ ...emptySearchOptions, query: "total", ...change });
const hit = (line: number, column = 5) => ({ line, column, length: 5, text: "let total = 1;", start: 4, shown: 5 });
const file = (path: string, ...lines: number[]): ProjectFileSearchEvent => ({ kind: "file", status: "ok", path, matches: lines.map(line => hit(line)), files: 0, matchCount: 0, truncated: false });
const done = (status = "ok", truncated = false): ProjectFileSearchEvent => ({ kind: "done", status, path: null, matches: null, files: 0, matchCount: 0, truncated });

test("a search is named by what it looks for; short or multi-line text is not searched", () => {
  assert.equal(searchKey(options()), searchKey(options({ include: "  " })));
  for (const change of [{ query: "Total" }, { regex: true }, { matchCase: true }, { wholeWord: true }, { include: "*.ts" }, { exclude: "bin" }] as Partial<SearchOptions>[])
    assert.notEqual(searchKey(options(change)), searchKey(options()), JSON.stringify(change));
  assert.deepEqual([searchable(options()), searchable(options({ query: "a" })), searchable(options({ query: "" })), searchable(options({ query: "a\nb" }))], [true, false, false, false]);
});

test("results grow file by file until the search is done", () => {
  let results = startSearch(options());
  assert.deepEqual([results.state, results.files, results.matches], ["searching", [], 0]);
  results = searchEvent(results, file("src/a.ts", 1, 4));
  results = searchEvent(results, { ...file("src/b.ts", 9), truncated: true });
  assert.deepEqual([results.state, results.files.map(value => value.path), results.matches, results.files[1].truncated], ["searching", ["src/a.ts", "src/b.ts"], 3, true]);
  const finished = searchEvent(results, done("ok", true));
  assert.deepEqual([finished.state, finished.truncated, finished.matches], ["done", true, 3]);
  // What comes after the end changes nothing.
  assert.equal(searchEvent(finished, file("src/c.ts", 1)), finished);
});

test("a search that the host ends early, or an answer that is not one, is a failure that says why", () => {
  const results = searchEvent(startSearch(options()), file("src/a.ts", 1));
  const failed = searchEvent(results, done("invalid_pattern"));
  assert.deepEqual([failed.state, failed.status, failed.files.length], ["failed", "invalid_pattern", 1]);
  assert.equal(searchFailure("invalid_pattern"), "The regular expression is not valid.");
  assert.equal(searchFailure("invalid_glob"), "A file pattern is not valid.");
  assert.equal(searchFailure("timeout"), "The search took too long and was stopped. Try a simpler expression.");
  assert.equal(searchFailure("unknown_project"), "The project folder is unavailable.");
  assert.equal(searchFailure("anything"), "The search could not be completed.");
  const malformed: ProjectFileSearchEvent[] = [file("src/a.ts", 2), { ...file("src/b.ts", 1), path: null }, { ...file("src/b.ts", 1), path: "" }, { ...file("src/b.ts"), matches: [] },
    { ...file("src/b.ts", 1), matches: null }, { ...file("src/b.ts", 1), status: "read_failed" }, { ...file("src/b.ts", 1), kind: "other" },
    { ...file("src/b.ts"), matches: [{ ...hit(1), line: 0 }] }, { ...file("src/b.ts"), matches: [{ ...hit(1), start: 12, shown: 9 }] },
    { ...file("src/b.ts"), matches: [{ ...hit(1), column: 1.5 }] }];
  for (const event of malformed) assert.deepEqual([searchEvent(results, event).state, searchEvent(results, event).status], ["failed", "invalid_response"], JSON.stringify(event));
});

test("the rows are each file and its matches, unless the file is folded; a match shows where it is in its line", () => {
  let results = searchEvent(searchEvent(startSearch(options()), file("src/a.ts", 1, 4)), file("b.ts", 7));
  results = searchEvent(results, done());
  const rows = searchRows(results, new Set(["b.ts"]));
  assert.deepEqual(rows.map(row => row.kind === "file" ? `${row.file.path}${row.collapsed ? " >" : ""}` : `  ${row.path}:${row.match.line}`),
    ["src/a.ts", "  src/a.ts:1", "  src/a.ts:4", "b.ts >"]);
  assert.equal(new Set(rows.map(row => row.key)).size, rows.length);
  assert.deepEqual(searchRows(null, new Set()), []);
  assert.deepEqual(matchParts(hit(1)), ["let ", "total", " = 1;"]);
  assert.deepEqual(matchParts({ ...hit(1), text: "total", start: 0, shown: 5 }), ["", "total", ""]);
});

test("only the end of a long start of line is shown before a match, from the start of a word", () => {
  const part = (text: string, word: string, lead?: number) => matchParts({ ...hit(1), text, start: text.indexOf(word), shown: word.length }, lead);
  const line = 'failed: !trashFailed ? null : bin ? "It could not be moved to the Recycle Bin." : null,';
  assert.deepEqual(part(line, "to the Recycle Bin"), ["…not be moved ", "to the Recycle Bin", '." : null,']);
  // What is kept is never longer than asked; a start that fits is shown whole, with the mark of the host for what it left out.
  assert.deepEqual(part(line, "to the Recycle Bin", 12), ["…be moved ", "to the Recycle Bin", '." : null,']);
  assert.deepEqual(part(line, "to the Recycle Bin", 200)[0], 'failed: !trashFailed ? null : bin ? "It could not be moved ');
  assert.deepEqual(part("…a long line = total;", "total"), ["…a long line = ", "total", ";"]);
  assert.deepEqual(part("…a very long line that goes on = total;", "total"), ["…that goes on = ", "total", ";"]);
  // A cut that falls on the start of a word keeps it; a word with nothing after it is cut where it has to be.
  assert.deepEqual(part("one two three four total", "total", 11), ["…three four ", "total", ""]);
  assert.deepEqual(part("aVeryLongIdentifierNameBeforeTheTotal", "Total", 8), ["…eforeThe", "Total", ""]);
  assert.deepEqual(part("some words, then-total", "total", 3), ["…-", "total", ""]);
  // A character of two code units is kept whole.
  assert.deepEqual(part("ab 😀😀😀 total", "total", 6), ["…😀😀 ", "total", ""]);
});
