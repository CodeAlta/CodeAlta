import assert from "node:assert/strict";
import test from "node:test";
import { defaultSplashColors, parseSplashAppearance, splashColorNames, splashDocument, splashMarkup, splashScript, splashStorageKey } from "./splashMarkup";

const logo = `<?xml version="1.0" encoding="UTF-8"?>\n<svg viewBox="0 0 512 512"><path d="M0 0"/></svg>`;
const light = { theme: "light", background: "#f6f7f9", surface: "#ffffff", text: "#1c2127", accent: "#2d72d2", line: "#d3d5d7" };

test("a stored appearance is a theme and its opaque colors", () => {
  assert.deepEqual(parseSplashAppearance(JSON.stringify(light)), light);
  assert.deepEqual(parseSplashAppearance(JSON.stringify({ ...light, background: "#F6F7F9" }))?.background, "#f6f7f9");
  assert.equal(parseSplashAppearance(null), null);
  assert.equal(parseSplashAppearance("not json"), null);
  assert.equal(parseSplashAppearance(JSON.stringify({ ...light, theme: "system" })), null);
  assert.equal(parseSplashAppearance(JSON.stringify({ ...light, accent: "blue" })), null);
  assert.equal(parseSplashAppearance(JSON.stringify({ ...light, line: undefined })), null);
});

test("the start-up screen needs nothing but its own document", () => {
  const markup = splashMarkup(logo);
  // The page allows no inline script: the screen is a style and elements, and its colors have defaults.
  assert.equal(/<script/i.test(markup), false);
  assert.equal(markup.includes("<?xml"), false, "the logo is inlined as an element");
  assert.equal(markup.split("<svg").length - 1, 2, "the mark in the title bar and the logo");
  for (const name of splashColorNames) assert.ok(markup.includes(`var(--splash-${name}, ${defaultSplashColors[name]})`), name);
  assert.ok(markup.includes('id="splash"'));

  const document = splashDocument(logo);
  assert.ok(document.startsWith("<!doctype html>"));
  assert.ok(document.includes(`<script src="./splash.js"></script>`), "its script is a file of the application");
  assert.ok(document.includes(markup));
});

test("the script gives the document the stored colors and ignores anything else", () => {
  const run = (stored: string | null) => {
    const properties = new Map<string, string>();
    const root = { dataset: {} as Record<string, string>, style: { setProperty: (name: string, value: string) => { properties.set(name, value); } } };
    const context = { localStorage: { getItem: (key: string) => key === splashStorageKey ? stored : null }, document: { documentElement: root } };
    new Function("localStorage", "document", splashScript)(context.localStorage, context.document);
    return { theme: root.dataset.theme, properties };
  };
  const applied = run(JSON.stringify(light));
  assert.equal(applied.theme, "light");
  assert.equal(applied.properties.get("--splash-background"), "#f6f7f9");
  assert.equal(applied.properties.size, splashColorNames.length);

  const partial = run(JSON.stringify({ theme: "neon", background: "url(x)", accent: "#2d72d2" }));
  assert.equal(partial.theme, undefined);
  assert.deepEqual([...partial.properties], [["--splash-accent", "#2d72d2"]]);
  assert.equal(run("not json").properties.size, 0);
  assert.equal(run(null).properties.size, 0);
});
