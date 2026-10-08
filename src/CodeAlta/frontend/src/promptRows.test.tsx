import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { AgentPromptEntry } from "#neoastra";
import { PromptRows } from "./PromptRows";
import { locales, translate } from "./localization";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
const prompt = (id: string, scope: string, change: Partial<AgentPromptEntry> = {}): AgentPromptEntry =>
  ({ id, name: id, description: null, kind: "Agent", scope, readOnly: scope !== "Global" && scope !== "Project", shadowed: false, shadowedByScope: null, systemPromptId: null, append: true, ...change });
function rows(prompts: readonly AgentPromptEntry[], change: { locale?: typeof locales[number]; open?: boolean; disabled?: boolean } = {}) {
  const locale = change.locale ?? "en";
  return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(PromptRows, { prompts, title: "Agent prompts", selected: null, disabled: change.disabled ?? false, onSelect: never, onOpen: change.open === false ? undefined : never })));
}
const cards = (html: string) => html.split('<div class="bp6-card').slice(1);

test("the row of a prompt opens its file in the code editor, as the row of a plugin does, in every language", () => {
  const prompts = [prompt("reviewer", "Global", { name: "Reviewer" }), prompt("house", "Project"), prompt("default", "BuiltIn", { name: "Default" }), prompt("planner", "CopilotGlobal")];
  for (const locale of locales) {
    const html = rows(prompts, { locale });
    assert.ok(html.includes(`>${translate(locale, "Agent prompts")}</h3>`), html);
    const [mine, local, shipped, copilot] = cards(html);
    for (const [row, name] of [[mine, "Reviewer"], [local, "house"], [copilot, "planner"]] as const) {
      assert.ok(row.includes(`aria-label="${translate(locale, "Edit {name}", { name })}"`) && row.includes(`title="${translate(locale, "Edit in the code editor")}"`), row);
    }
    // A prompt that ships with CodeAlta is read: its file is opened, not edited.
    assert.ok(shipped.includes(`title="${translate(locale, "Open in the code editor")}"`) && !shipped.includes(translate(locale, "Edit {name}", { name: "Default" })), shipped);
    assert.ok(shipped.includes(`aria-label="${translate(locale, "Open in the code editor")}: Default"`), shipped);
  }
});

test("a list without prompts shows nothing, and a page that opens no editor has rows without a button", () => {
  assert.equal(rows([]), "");
  const closed = rows([prompt("reviewer", "Global")], { open: false });
  assert.ok(closed.includes("<strong>reviewer</strong>") && !closed.includes("<button"), closed);
  assert.match(rows([prompt("reviewer", "Global")], { disabled: true }), /<button[^>]*disabled=""/);
});
