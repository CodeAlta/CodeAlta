import assert from "node:assert/strict";
import test from "node:test";
import { createDiagramCache, maximumDiagramLength, type DiagramAppearance } from "./diagrams";
import { mixColors } from "./shellColors";

const dark: DiagramAppearance = { key: "dark|", variables: {} };
const light: DiagramAppearance = { key: "light|", variables: {} };

test("a diagram is drawn once per text and appearance, then found by later renders", async () => {
  const calls: string[] = [];
  const cache = createDiagramCache(async (text, appearance) => { calls.push(`${appearance.key}${text}`); return `<svg>${text}</svg>`; });

  assert.equal(cache.get("graph TD; A-->B", dark), undefined, "not drawn yet");
  await Promise.all([cache.draw("graph TD; A-->B", dark), cache.draw("graph TD; A-->B", dark)]);
  assert.equal(cache.get("graph TD; A-->B", dark), "<svg>graph TD; A-->B</svg>");
  await cache.draw("graph TD; A-->B", dark);
  assert.deepEqual(calls, ["dark|graph TD; A-->B"]);

  // Another theme or color scheme is another drawing.
  assert.equal(cache.get("graph TD; A-->B", light), undefined);
  await cache.draw("graph TD; A-->B", light);
  assert.equal(calls.length, 2);
});

test("text that is not a diagram is remembered as such and never retried", async () => {
  let calls = 0;
  const cache = createDiagramCache(async text => { calls++; if (text === "throws") throw new Error("parser"); return null; });

  await cache.draw("not a diagram", dark);
  await cache.draw("throws", dark);
  assert.equal(cache.get("not a diagram", dark), null);
  assert.equal(cache.get("throws", dark), null);
  await cache.draw("not a diagram", dark);
  assert.equal(calls, 2);

  // Empty and oversized text is never handed to the drawer.
  assert.equal(cache.get("  \n", dark), null);
  assert.equal(cache.get("x".repeat(maximumDiagramLength + 1), dark), null);
  await cache.draw("x".repeat(maximumDiagramLength + 1), dark);
  assert.equal(calls, 2);
});

test("the least recently used diagrams are dropped beyond the capacity", async () => {
  const cache = createDiagramCache(async text => `<svg>${text}</svg>`, 2);
  await cache.draw("a", dark);
  await cache.draw("b", dark);
  assert.equal(cache.get("a", dark), "<svg>a</svg>"); // "a" is now the most recently used.
  await cache.draw("c", dark);
  assert.equal(cache.get("b", dark), undefined);
  assert.equal(cache.get("a", dark), "<svg>a</svg>");
  assert.equal(cache.get("c", dark), "<svg>c</svg>");
});

test("colors mix channel by channel", () => {
  assert.equal(mixColors("#000000", "#ffffff", 0.5), "#808080");
  assert.equal(mixColors("#102030", "#ff0000", 0), "#102030");
  assert.equal(mixColors("#102030", "#ff0000", 1), "#ff0000");
});
