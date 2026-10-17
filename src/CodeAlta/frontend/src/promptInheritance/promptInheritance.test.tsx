import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { AgentPromptEntry } from "#neoastra";
import { inheritCandidates, maximumInherits, movedEarlier, promptInEffect, tokenCount, withInherited } from "./inheritance";
import { PromptInheritance, type PromptComposition } from "./PromptInheritance";
import { locales, translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";

const never = () => assert.fail("rendering must not act");
const prompt = (id: string, scope: string, change: Partial<AgentPromptEntry> = {}): AgentPromptEntry =>
  ({ id, name: id, description: null, kind: "Agent", scope, readOnly: scope === "BuiltIn", shadowed: false, shadowedByScope: null, systemPromptId: null,
    append: false, inherits: [], ...change });
const prompts = [
  prompt("default", "BuiltIn", { name: "Default", shadowed: true }), prompt("plan", "BuiltIn", { name: "Plan" }),
  prompt("default", "Global", { name: "My default" }), prompt("maui", "Global", { name: "MAUI", description: "MAUI rules" }),
  prompt("maui-default", "Global", { name: "MAUI Default", inherits: ["default", "maui"] }), prompt("strict", "Global", { kind: "System" }),
];
const composition: PromptComposition = { status: "ok", parts: [{ id: "default", name: "My default", scope: "Global", tokens: 1200 }, { id: "maui", name: "MAUI", scope: "Global", tokens: 300 }],
  missing: ["gone"], circular: [], systemPromptId: "maui-system", disabledTools: ["webget"], body: "Default\n\nMAUI\n\nOwn", tokens: 1550, truncated: false };

test("the prompt in effect for an id is the one no other file replaces, and each agent prompt is offered once", () => {
  assert.equal(promptInEffect(prompts, "default")?.scope, "Global");
  assert.equal(promptInEffect(prompts, "DEFAULT")?.name, "My default");
  assert.equal(promptInEffect(prompts, "strict"), null, "a system prompt is not inherited");
  assert.deepEqual(inheritCandidates(prompts, "maui-default", ["default"]).map(candidate => candidate.id), ["plan", "maui"]);
  assert.deepEqual(inheritCandidates(prompts, "", []).map(candidate => [candidate.id, candidate.name]),
    [["default", "My default"], ["plan", "Plan"], ["maui", "MAUI"], ["maui-default", "MAUI Default"]]);
});

test("a prompt is inherited once, at most as many as the host takes, and moves one place earlier", () => {
  assert.deepEqual(withInherited(["default"], " maui "), ["default", "maui"]);
  const list = ["default"];
  assert.equal(withInherited(list, "Default"), list);
  const full = Array.from({ length: maximumInherits }, (_, index) => `p${index}`);
  assert.equal(withInherited(full, "more"), full);
  assert.deepEqual(movedEarlier(["a", "b", "c"], 2), ["a", "c", "b"]);
  assert.deepEqual(movedEarlier(["a", "b"], 0), ["a", "b"]);
  assert.deepEqual([tokenCount(950), tokenCount(1000), tokenCount(1250), tokenCount(12_400)], ["950", "1k", "1.3k", "12k"]);
});

function render(change: Partial<Parameters<typeof PromptInheritance>[0]> = {}, locale: typeof locales[number] = "en") {
  return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(PromptInheritance, { promptId: "maui-default", inherits: ["default", "maui", "gone"], prompts, composition,
      onChange: never, onShow: never, onPreview: never, ...change })));
}

test("the chain shows the inherited prompts in order, then this prompt, in every language", () => {
  for (const locale of locales) {
    const html = render({}, locale);
    const steps = html.split('class="prompt-inheritance-step"').slice(1);
    assert.equal(steps.length, 4, html);
    assert.match(steps[0], /<strong>My default<\/strong><code>default<\/code>/);
    assert.ok(steps[1].includes(`aria-label="${translate(locale, "Move {name} earlier", { name: "MAUI" })}"`), steps[1]);
    assert.ok(!steps[0].includes(translate(locale, "Move {name} earlier", { name: "My default" })), "the first prompt cannot come earlier");
    assert.ok(steps[2].includes('data-state="missing"') && steps[2].includes(translate(locale, "Not found")), steps[2]);
    assert.ok(steps[3].includes('data-self="true"') && steps[3].includes(translate(locale, "This prompt")), steps[3]);
    assert.ok(html.includes(translate(locale, "{count} prompts · about {tokens} tokens", { count: "3", tokens: "1.6k" })), html);
    assert.ok(html.includes(translate(locale, "Inherit a prompt")));
  }
});

test("a read-only prompt shows its chain without the buttons that change it, and a prompt that inherits nothing says so", () => {
  const html = render({ readOnly: true });
  assert.ok(!html.includes("Stop inheriting") && !html.includes("Move ") && !html.includes("Inherit a prompt"), html);
  const none = render({ inherits: [], composition: null });
  assert.ok(none.includes("prompt-inheritance-none") && none.includes("Inherit a prompt") && !none.includes("This prompt"), none);
});
