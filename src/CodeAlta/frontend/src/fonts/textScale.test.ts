import assert from "node:assert/strict";
import test from "node:test";
import { scaledTextSizes } from "./textScale";

const s = (size: string) => `calc(${size} * var(--ui-text-scale, 1))`;

test("the sizes of the text in px and rem follow the interface text size", () => {
  assert.equal(scaledTextSizes(".a { color: red; font-size: 12px; }"), `.a { color: red; font-size: ${s("12px")}; }`);
  assert.equal(scaledTextSizes(".a{font-size:.75rem}.b{font-size:11.5px !important}"), `.a{font-size:${s(".75rem")}}.b{font-size:${s("11.5px")} !important}`);
  assert.equal(scaledTextSizes(".a { font-size: var(--bp-typography-size-body-medium) }"), `.a { font-size: ${s("var(--bp-typography-size-body-medium)")} }`);
  assert.equal(scaledTextSizes("@media (max-width: 600px) { .a { font-size: 13px } }"), `@media (max-width: 600px) { .a { font-size: ${s("13px")} } }`);
});

test("the size in a font shorthand is scaled, and its line height and family are kept", () => {
  assert.equal(scaledTextSizes(".a { font: 12px/1.6 var(--code-font); }"), `.a { font: ${s("12px")}/1.6 var(--code-font); }`);
  assert.equal(scaledTextSizes(".a { font: 600 10.5px/1 var(--code-font) }"), `.a { font: 600 ${s("10.5px")}/1 var(--code-font) }`);
  assert.equal(scaledTextSizes(".a { font: 11px var(--code-font) }"), `.a { font: ${s("11px")} var(--code-font) }`);
  assert.equal(scaledTextSizes(".a { font: var(--bp-typography-size-code-small)/var(--bp-typography-line-height-large) var(--bp-typography-family-mono) }"),
    `.a { font: ${s("var(--bp-typography-size-code-small)")}/var(--bp-typography-line-height-large) var(--bp-typography-family-mono) }`);
});

test("relative sizes, computed sizes, custom properties and the other font properties are left alone", () => {
  const kept = [
    ".a { font-size: .9em; font-size: 90%; font-size: inherit; font-size: smaller; font-size: 0 }",
    ".a { font: inherit; font: var(--font); font: 500 1em/1.2 serif }",
    ".a { font: clamp(3px, 1.8cqw, 12px)/1 var(--code-font-builtin); font-size: calc(1em + 2px); font-size: min(12px, 3vw) }",
    ":root { --bp-typography-size-body-medium: 14px; --code-font-size: 13px; --x-font-size: 12px }",
    ".a { font-size: var(--code-font-size); font: var(--code-font-size)/20px var(--code-font) }",
    ".a { font-family: \"Fira Code\"; font-weight: 600; font-variant-numeric: tabular-nums; -webkit-font-smoothing: antialiased; line-height: 17px }",
  ];
  for (const css of kept) assert.equal(scaledTextSizes(css), css);
});

test("a selector that names the font element is not a declaration", () => {
  const css = "font:first-child { color: red } .a > font:hover{font-size:12px}";
  assert.equal(scaledTextSizes(css), `font:first-child { color: red } .a > font:hover{font-size:${s("12px")}}`);
});
