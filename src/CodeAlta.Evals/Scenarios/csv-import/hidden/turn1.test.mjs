import { test } from "node:test";
import assert from "node:assert/strict";

const root = process.env.EVAL_PROJECT_URL;
const { parseCsv, CsvError } = await import(new URL("src/csv.js", root));
const error = (text, options) => { try { parseCsv(text, options); } catch (e) { assert.ok(e instanceof CsvError, String(e)); return [e.line, e.column]; } return null; };

test("records and fields", () => {
  assert.deepEqual(parseCsv("a,b,c\n1,2,3"), [["a", "b", "c"], ["1", "2", "3"]]);
  assert.deepEqual(parseCsv("a,b\r\n1,2\r\n"), [["a", "b"], ["1", "2"]]);
  assert.deepEqual(parseCsv(" a , b "), [[" a ", " b "]]);
  assert.deepEqual(parseCsv("a,,c"), [["a", "", "c"]]);
  assert.deepEqual(parseCsv(""), []);
});

test("quoted fields hold commas, doubled quotes and line breaks", () => {
  assert.deepEqual(parseCsv('"a,b","say ""hi""",c'), [["a,b", 'say "hi"', "c"]]);
  assert.deepEqual(parseCsv('"line1\nline2",x\ny,z'), [["line1\nline2", "x"], ["y", "z"]]);
  assert.deepEqual(parseCsv('"",x'), [["", "x"]]);
});

test("empty lines are skipped, not inside quotes", () => {
  assert.deepEqual(parseCsv("a,b\n\n1,2\n\n"), [["a", "b"], ["1", "2"]]);
  assert.deepEqual(parseCsv('"x\n\ny",z'), [["x\n\ny", "z"]]);
});

test("a header names the fields", () => {
  assert.deepEqual(parseCsv("id,name\n1,Ann\n2,Bob", { header: true }), [{ id: "1", name: "Ann" }, { id: "2", name: "Bob" }]);
  assert.deepEqual(parseCsv("id,name", { header: true }), []);
  assert.deepEqual(parseCsv("", { header: true }), []);
});

test("errors give the line and the column of their cause, from 1", () => {
  assert.deepEqual(error('a,b"c'), [1, 4]);
  assert.deepEqual(error('a,"b"c'), [1, 6]);
  assert.deepEqual(error('a,b\nc,"d'), [2, 3]);
  assert.deepEqual(error('"x\ny"z,1'), [2, 3]);
  assert.deepEqual(error("a,b\n1,2,3"), [2, 1]);
  assert.deepEqual(error('a,b\n"1\n2",x\n3,4,5'), [4, 1]);
  assert.deepEqual(error("id,name,id", { header: true }), [1, 9]);
});
