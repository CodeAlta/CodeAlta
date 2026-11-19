import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { costlyHasSelectors, monacoHoverStyle, negatedStateAncestors } from "./styleInvalidation";

test("a negated hover or focus on an ancestor is found, and nothing else", () => {
  assert.deepEqual(negatedStateAncestors(`
    .row:not(:hover):not(:focus-within) .actions > button { display: none; }
    /* .gone:not(:hover) .x { } */
    @media (max-width: 720px) { .a, .list:not(:focus-within).locked .pointer::after { color: red; } }
    .card:not(.open, :hover) .tools { opacity: 0; }`),
  [".row:not(:hover):not(:focus-within) .actions > button", ".list:not(:focus-within).locked .pointer::after", ".card:not(.open, :hover) .tools"]);
  assert.deepEqual(negatedStateAncestors(`
    .row:hover .action, .row:focus-within .action { opacity: 1; }
    .action:not(:hover) { opacity: 0; }
    .shell .row:not(:hover, :focus-within) > .action { visibility: hidden; }
    .input:not(:focus) + .button .icon { color: gray; }
    .list:not(.locked) .item:hover { color: blue; }`), []);
});

function stylesheets(folder: URL): URL[] {
  return readdirSync(folder, { withFileTypes: true }).flatMap(entry => entry.isDirectory() ? stylesheets(new URL(`${entry.name}/`, folder))
    : entry.name.endsWith(".css") ? [new URL(entry.name, folder)] : []);
}

test("no stylesheet of the application negates the hover or the focus on an ancestor", () => {
  const root = new URL("./", import.meta.url);
  const found = stylesheets(root).flatMap(file => negatedStateAncestors(readFileSync(file, "utf8"))
    .map(selector => `${fileURLToPath(file).slice(fileURLToPath(root).length)}: ${selector}`));
  assert.deepEqual(found, []);
});

test("a :has() that costs every change of the page is found, and nothing else", () => {
  assert.deepEqual(costlyHasSelectors(`
    dialog:has(> .window) > .window > .body > * { min-width: 0; }
    dialog.info:has(> .window) .fields dd small, .card:has(+ .card) p { margin: 0; }
    /* .gone:has(.x) p { } */
    @media (max-width: 720px) { .heading:has(.tag) .open > svg, .panes:has(.pane) .pane > :is(pre, .code) { color: red; } }
    .ide-shell:has(.window-zoom) { --width: 142px; }
    body:has(dialog[open]) { overflow: hidden; }`),
  ["dialog:has(> .window) > .window > .body > *", "dialog.info:has(> .window) .fields dd small", ".card:has(+ .card) p", ".heading:has(.tag) .open > svg",
    ".panes:has(.pane) .pane > :is(pre, .code)", ".ide-shell:has(.window-zoom)", "body:has(dialog[open])"]);
  assert.deepEqual(costlyHasSelectors(`
    dialog[open] > .window > .body > *, dialog > .window .fields dd { min-width: 0; }
    .row:has(> button[aria-pressed="true"]), pre:has(> .copy)::before, .folder:not(:has(> span)) { color: blue; }
    .area:has(> .rail) > .scroll, a:has(> img[src]) > .alt, .panes:has(.pane) .pane > :is(.preview, .code) { display: none; }
    .ide-shell .workspace-shell:has(> .window-controls) { --inset: 138px; }
    .ide-shell[data-window-zoom] .tabs:has(.tab) { padding: 0; }`), []);
});

test("no stylesheet of the application holds a :has() that costs every change of the page", () => {
  const root = new URL("./", import.meta.url);
  const found = stylesheets(root).flatMap(file => costlyHasSelectors(readFileSync(file, "utf8"))
    .map(selector => `${fileURLToPath(file).slice(fileURLToPath(root).length)}: ${selector}`));
  assert.deepEqual(found, []);
});

test("Monaco's hover stylesheet loses its negated focus, and only that", () => {
  const source = readFileSync(new URL("../node_modules/monaco-editor/esm/vs/platform/hover/browser/hover.css", import.meta.url), "utf8");
  // A new version of Monaco that words the rule another way has to be looked at again.
  assert.equal(negatedStateAncestors(source).length, 1);
  const css = monacoHoverStyle(source);
  assert.deepEqual(negatedStateAncestors(css), []);
  assert.equal(css.length, source.length - ":not(:focus-within)".length);
  assert.ok(css.includes(".workbench-hover-container.locked .workbench-hover-pointer:after {"));
  // The rule of a hover with the focus still follows it, and wins with the same specificity.
  assert.ok(css.indexOf(".workbench-hover-container.locked .workbench-hover-pointer:after") < css.indexOf(".workbench-hover-container:focus-within .workbench-hover-pointer:after"));
});
