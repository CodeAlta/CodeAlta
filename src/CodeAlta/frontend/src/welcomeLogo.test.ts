import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { welcomeLogo } from "./welcomeLogo";

test("web welcome renders the actual TUI 3-D font with the same words, spacing and trimming", () => {
  const font = readFileSync(new URL("../../../CodeAlta.Tui/Assets/3d.flf", import.meta.url), "utf8");
  const logo = welcomeLogo(font);
  assert.equal(logo.code.split("\n").length, 8);
  assert.equal(logo.alta.split("\n").length, 8);
  assert.ok(logo.code.startsWith("   ██████ "));
  assert.ok(logo.code.includes("░"));
  assert.ok(logo.alta.includes("█"));
  for (const word of [logo.code, logo.alta]) {
    assert.doesNotMatch(word, /[@$]/);
    assert.ok(word.split("\n").every(line => line === line.trimEnd()));
  }
});
