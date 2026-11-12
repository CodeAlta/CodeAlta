import assert from "node:assert/strict";
import test from "node:test";
import { textFieldEdits, textFieldOf } from "./textFieldEdits";

// An element as `textFieldOf` reads it: its tag, its type, and whether it is in an editor of Monaco or a terminal.
const element = (tagName: string, type?: string, inside?: string) => ({ tagName, type, closest: (selector: string) => inside && selector.includes(inside) ? {} : null });

test("a right-click on a field where text is typed is one on a text field, not one on another control", () => {
  for (const type of ["text", "search", "url", "tel", "email", "password", "number", "TEXT"]) assert.ok(textFieldOf(element("INPUT", type) as never), type);
  assert.ok(textFieldOf(element("TEXTAREA", "textarea") as never), "textarea");
  for (const type of ["checkbox", "radio", "button", "color", "range", "file"]) assert.equal(textFieldOf(element("INPUT", type) as never), null, type);
  assert.equal(textFieldOf(element("DIV") as never), null);
  assert.equal(textFieldOf(null), null);
});

test("the text area of an editor of Monaco or of a terminal is not a text field of the page", () => {
  assert.equal(textFieldOf(element("TEXTAREA", "textarea", ".monaco-editor") as never), null);
  assert.equal(textFieldOf(element("TEXTAREA", "textarea", ".xterm") as never), null);
});

test("a field that can be edited cuts and copies what is selected, and always pastes", () => {
  const field = { hidden: false, length: 11, writable: true };
  assert.deepEqual(textFieldEdits(field, true), { cut: true, copy: true, paste: true, selectAll: true });
  assert.deepEqual(textFieldEdits(field, false), { cut: false, copy: false, paste: true, selectAll: true });
  assert.deepEqual(textFieldEdits({ ...field, length: 0 }, false), { cut: false, copy: false, paste: true, selectAll: false });
});

test("a read-only or disabled field, or editor, is copied and selected, never cut or pasted into", () => {
  assert.deepEqual(textFieldEdits({ hidden: false, length: 5, writable: false }, true), { cut: false, copy: true, paste: false, selectAll: true });
  assert.deepEqual(textFieldEdits({ hidden: false, length: 5, writable: false }, false), { cut: false, copy: false, paste: false, selectAll: true });
});

test("the text of a password is neither cut nor copied", () => {
  assert.deepEqual(textFieldEdits({ hidden: true, length: 6, writable: true }, true), { cut: false, copy: false, paste: true, selectAll: true });
  assert.deepEqual(textFieldEdits({ hidden: true, length: 6, writable: false }, true), { cut: false, copy: false, paste: false, selectAll: true });
});
