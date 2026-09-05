import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("production entrypoint is a generated boot contract, not a native smoke fixture", async () => {
  const source = await readFile(new URL("./main.tsx", import.meta.url), "utf8");
  assert.match(source, /boot\.status/);
  assert.match(source, /StrictMode/);
  assert.doesNotMatch(source, /probe\.|localStorage|sessionStorage|indexedDB|fetch\(/);
  const html = await readFile(new URL("../index.html", import.meta.url), "utf8");
  assert.match(html, /id="root"/);
});
