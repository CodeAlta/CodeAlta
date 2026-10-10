import assert from "node:assert/strict";
import test from "node:test";
import { createIconFiles, createIconLoader, isIconFile, libraryExportName } from "./pluginIcons";

test("the name of a library icon is its kebab-case name written as the exports of the library write it", () => {
  assert.equal(libraryExportName("chart-column"), "ChartColumn");
  assert.equal(libraryExportName("building-2"), "Building2");
  assert.equal(libraryExportName("a-arrow-down"), "AArrowDown");
  assert.equal(libraryExportName("star"), "Star");
  for (const bad of ["", "Chart-Column", "chart_column", "chart--column", "-star", "star-", "icons/star.svg", "star.svg", "a".repeat(65)]) assert.equal(libraryExportName(bad), null, bad);
});

test("an icon names a file of the plugin when it is a path or ends in svg", () => {
  assert.equal(isIconFile("icons/statistics.svg"), true);
  assert.equal(isIconFile("statistics.SVG"), true);
  assert.equal(isIconFile("icons\\a"), true);
  assert.equal(isIconFile("chart-column"), false);
  assert.equal(isIconFile("github"), false);
});

test("the library loads once, when an icon first needs it, and a name it does not know is null", async () => {
  let loads = 0;
  const Star = () => null;
  const loader = createIconLoader(async () => { loads++; return { Star, ChartColumn: Object.assign(() => null, { displayName: "chart" }), NotAnIcon: 3 }; });
  assert.equal(loader.peek("star"), undefined, "nothing is known before the library is loaded");
  assert.equal(await loader.resolve("star"), Star);
  assert.ok(await loader.resolve("chart-column"));
  assert.equal(await loader.resolve("missing-icon"), null);
  assert.equal(await loader.resolve("not-an-icon"), null, "an export that is not a component is not an icon");
  assert.equal(await loader.resolve("Not Valid"), null);
  assert.equal(await loader.resolve("constructor"), null);
  assert.equal(loads, 1);
  assert.equal(loader.peek("star"), Star);
  assert.equal(loader.peek("missing-icon"), null);
});

test("a library that cannot be loaded draws no icon and is asked again next time", async () => {
  let attempts = 0;
  const loader = createIconLoader(async () => { attempts++; if (attempts === 1) throw new Error("offline"); return { Star: () => null }; });
  assert.equal(await loader.resolve("star"), null);
  assert.ok(await loader.resolve("star"));
  assert.equal(attempts, 2);
});

test("the icon files of plugins are found by plugin and path, and listeners hear a change once", () => {
  const files = createIconFiles();
  let heard = 0;
  const stop = files.subscribe(() => heard++);
  assert.equal(files.get("p", "icons/a.svg"), null);
  files.register("p", "icons/a.svg", "data:image/svg+xml;base64,AA==");
  files.register("p", "icons/a.svg", "data:image/svg+xml;base64,AA==");
  assert.equal(files.get("p", "icons/a.svg"), "data:image/svg+xml;base64,AA==");
  assert.equal(files.get("q", "icons/a.svg"), null, "another plugin has no such file");
  assert.equal(heard, 1);
  files.register("p", "star", null);
  files.register("p", null, "data:x");
  assert.equal(heard, 1);
  files.register("p", "icons/a.svg", null);
  assert.equal(files.get("p", "icons/a.svg"), null);
  assert.equal(heard, 2);
  stop();
  files.register("p", "icons/b.svg", "data:image/svg+xml;base64,BB==");
  assert.equal(heard, 2);
  assert.equal(files.get(null, "icons/b.svg"), null);
});
