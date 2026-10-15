import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { GlobalConfigModelTiersResponse } from "#neoastra";
import { createTierStore, readTierState } from "../modelTiers";
import { ShellLanguageContext } from "../shellLanguage";
import { SpaceModelTiers } from "./SpaceModelTiers";
import { readAssignments, type Space } from "./spaces";
import type { SpacesHub } from "./spacesHub";

const never = () => assert.fail("rendering must not act");
const space: Space = { id: "business", name: "Business", description: null, icon: null, color: null, isDefault: false, projectIds: [], file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] };
const reply = (source: "global" | "space"): GlobalConfigModelTiersResponse => ({
  status: "ok", message: null, source, spaceId: "business",
  fallback: { enabled: true, onQuota: true, onUnavailable: true },
  tiers: [
    { tier: "complex", targets: [{ providerKey: "claude-code", modelId: "opus", reasoning: ["High"] }],
      choice: { providerKey: "claude-code", modelId: "opus", reasoning: "High", providerName: "Claude Code", modelName: "Opus" } },
    { tier: "standard", targets: [], choice: null },
    { tier: "quick", targets: source === "space" ? [{ providerKey: "claude-code", modelId: "haiku", reasoning: ["Low"] }] : [], choice: null },
  ],
});

function render(source: "global" | "space") {
  const store = createTierStore(() => Promise.reject(new Error("not read")));
  store.set("epoch", readTierState(reply(source))!, { spaceId: "business" });
  const snapshot = { spaces: [space], assignments: readAssignments({ status: "ok", providers: [{ key: "codex", spaces: ["personal"] }], mcpServers: [], plugins: [], skills: [], prompts: [] }) };
  const hub = { hostEpoch: () => "epoch", subscribe: () => () => {}, getSnapshot: () => snapshot } as unknown as SpacesHub;
  return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(SpaceModelTiers, { space, spaces: [space], hub, canEdit: true, busy: false, run: never, store, api: { saveModelTiers: never } })));
}

test("a space on the global tiers says what they give, and one with tiers of its own edits them with its own fallback", () => {
  const global = render("global");
  assert.match(global, /<option value="global" selected="">Use global<\/option>/);
  assert.match(global, /Complex: Opus · High/);
  assert.doesNotMatch(global, /model-tier-columns/);
  assert.doesNotMatch(global, /Fall back to the next model/, "The usual settings have their fallback on Settings > Models.");

  const own = render("space");
  assert.match(own, /<option value="space" selected="">Customize<\/option>/);
  assert.match(own, /class="model-tier-settings" data-space="true" aria-label="Model tiers"/);
  assert.match(own, /<strong>haiku<\/strong>/);
  // The space has a fallback of its own beside its tiers.
  assert.match(own, /Fall back to the next model of the tier when a provider fails/);
  assert.doesNotMatch(own, /id="model-tiers-title"/);
});
