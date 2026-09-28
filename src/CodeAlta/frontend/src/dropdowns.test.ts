import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import test from "node:test";

test("every native dropdown is rendered by Blueprint HTMLSelect", () => {
  const directory = new URL("./", import.meta.url);
  for (const name of readdirSync(directory).filter(name => name.endsWith(".tsx"))) {
    const source = readFileSync(new URL(name, directory), "utf8");
    assert.doesNotMatch(source, /<select\b/, name);
    if (/<HTMLSelect\b/.test(source)) assert.match(source, /import \{[^}]*\bHTMLSelect\b[^}]*\} from "@blueprintjs\/core"/, name);
  }
});

test("Blueprint's official CSS loads before application styles", () => {
  const main = readFileSync(new URL("./main.tsx", import.meta.url), "utf8");
  assert.match(main, /import "normalize\.css";\s*import "@blueprintjs\/core\/lib\/css\/blueprint\.css";\s*import "flexlayout-react\/style\/light\.css";\s*import "\.\/style\.css";/);
  const css = readFileSync(new URL("./style.css", import.meta.url), "utf8");
  assert.doesNotMatch(css, /\.bp6-html-select\s*>\s*\.bp6-icon/, "Do not recreate Blueprint's caret styling");
});
