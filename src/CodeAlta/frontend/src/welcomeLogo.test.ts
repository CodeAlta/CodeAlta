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
  const tui = readFileSync(new URL("../../../CodeAlta.Tui/Presentation/Shell/WelcomePaneFactory.cs", import.meta.url), "utf8");
  assert.match(tui, /CodeAlta\.Assets\.3d\.flf/);
  assert.match(tui, /TextFiglet\("Code"\)/);
  assert.match(tui, /TextFiglet\("Alta"\)/);
  assert.match(tui, /LetterSpacing\(1\)/);
  assert.match(tui, /Spacing = 2/);
  const config = readFileSync(new URL("../vite.config.ts", import.meta.url), "utf8");
  assert.match(config, /allow: \["\.\.", fileURLToPath\(new URL\("\.\.\/\.\.\/CodeAlta\.Tui\/Assets\/3d\.flf"/);
});

test("owned and new-session composers share the surface, toolbar, editor and keyboard contracts", () => {
  const owned = readFileSync(new URL("./OwnedSessionPanel.tsx", import.meta.url), "utf8");
  const draft = readFileSync(new URL("./ReadOnlyComposer.tsx", import.meta.url), "utf8");
  for (const component of [owned, draft]) {
    assert.match(component, /<ComposerSurface/);
    assert.match(component, /<ComposerToolbar/);
    assert.match(component, /<PromptEditor/);
    assert.match(component, /<ExpandedPromptEditor/);
    assert.match(component, /dispatchComposerKey/);
    assert.match(component, /isComposing/);
  }
  assert.match(draft, /className=\{localDraft \? undefined : "catalog-composer"\}/);
  assert.doesNotMatch(draft, /Draft a prompt — no session created yet/);
});
