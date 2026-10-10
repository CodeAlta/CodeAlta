import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import test from "node:test";
import { isScriptPath } from "../pluginScript/scriptModule";
import { appModuleFile, appModules } from "./appModules";

test("the application modules are named as the host accepts, have an entry file, and are served from the address the page loads", () => {
  assert.equal(new Set(appModules.map(module => module.name)).size, appModules.length, "one entry for each name");
  for (const module of appModules) {
    assert.match(module.name, /^[a-z][a-z0-9-]{0,63}$/u, module.name);
    assert.ok(existsSync(new URL(`../../${module.entry}`, import.meta.url)), `the entry of ${module.name} exists`);
    assert.equal(appModuleFile(module.name), `lib/app/${module.name}.js`);
    assert.ok(isScriptPath(`/${appModuleFile(module.name)}`), "the page accepts the path the host gives");
  }
});

test("the page accepts the path of an application module and only that shape of path outside a plugin folder", () => {
  assert.ok(isScriptPath("/lib/app/statistics.js"));
  assert.ok(isScriptPath("/lib/app/a-1.js"));
  for (const bad of ["/lib/app/Statistics.js", "/lib/app/1a.js", "/lib/app/a/b.js", "/lib/app/../x.js", "/lib/app/a.js?x", "/lib/react.js", "/lib/app/.js", "lib/app/a.js", "/assets/index.js", `/lib/app/${"a".repeat(65)}.js`]) {
    assert.equal(isScriptPath(bad), false, bad);
  }
});
