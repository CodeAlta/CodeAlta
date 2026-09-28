import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import test from "node:test";

test("every native dropdown is rendered by Blueprint HTMLSelect", () => {
  const directory = new URL("./", import.meta.url);
  for (const name of readdirSync(directory).filter(name => name.endsWith(".tsx"))) {
    const source = readFileSync(new URL(name, directory), "utf8");
    assert.doesNotMatch(source, /<select\b/, name);
    if (/<HTMLSelect\b/.test(source)) assert.match(source, /import \{ HTMLSelect \} from "@blueprintjs\/core"/, name);
  }
});
