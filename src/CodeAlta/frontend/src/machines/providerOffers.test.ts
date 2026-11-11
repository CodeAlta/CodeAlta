import assert from "node:assert/strict";
import test from "node:test";
import type { MachineProviderOfferView } from "#neoastra";
import { offerGroups, offerMatches, offerNeed, offerProgram, offersOfTypes, programAction, programLabel } from "./providerOffers";

const offer = (values: Partial<MachineProviderOfferView>): MachineProviderOfferView => ({ id: "x", type: "x", name: "X", group: "key", credential: "key", signIn: false,
  description: null, icon: null, program: null, unavailable: null, catalog: null, configuredKey: null, ...values });

test("the offers of a machine are grouped, the agents first, and filtered by the words typed", () => {
  const offers = [offer({ id: "anthropic", name: "Anthropic" }), offer({ id: "claude-code", name: "Claude Code", group: "agent", credential: "cli", description: "Anthropic's agent." }),
    offer({ id: "copilot", name: "GitHub Copilot", group: "subscription", credential: "sign_in" }), offer({ id: "odd", group: "made-up" })];
  assert.deepEqual(offerGroups(offers).map(group => [group.id, group.offers.map(value => value.id)]),
    [["agent", ["claude-code"]], ["subscription", ["copilot"]], ["key", ["anthropic", "odd"]]], "an unknown group goes with the APIs");
  assert.deepEqual(offers.filter(value => offerMatches(value, "anthropic agent")).map(value => value.id), ["claude-code"]);
  assert.equal(offers.filter(value => offerMatches(value, "")).length, 4);
});

test("an offer says what it needs and what the machine installs or runs", () => {
  assert.equal(offerNeed({ credential: "key", group: "key" }), "Takes an API key");
  assert.equal(offerNeed({ credential: "cli", group: "agent" }), "Signs in with its own account");
  assert.equal(offerNeed({ credential: "none", group: "local" }), "Runs on a model server of the machine");
  assert.deepEqual(offerProgram({ program: { state: "missing", version: "2.1.322", update: null, detail: null } }), { key: "Installs version {version}", values: { version: "2.1.322" } });
  assert.deepEqual(offerProgram({ program: { state: "runner", version: "0.9.0", update: null, detail: "uvx" } }), { key: "Runs with {runner}", values: { runner: "uvx" } });
  assert.deepEqual(offerProgram({ program: { state: "found", version: null, update: null, detail: "/usr/bin/claude" } }), { key: "Found on the machine" });
  assert.equal(offerProgram({ program: { state: "unsupported", version: null, update: null, detail: "no build" } }), null, "its reason is the unavailable one");
  assert.equal(offerProgram({ program: null }), null);
});

test("the row of a provider says what it runs, and offers its install or its update", () => {
  assert.deepEqual(programLabel({ state: "installed", version: "2.1.322", update: null, detail: "/x" }), { key: "Version {version}, installed by the machine", values: { version: "2.1.322" } });
  assert.deepEqual(programLabel({ state: "found", version: null, update: null, detail: "/usr/bin/claude" }), { key: "Runs {path}", values: { path: "/usr/bin/claude" } });
  assert.deepEqual(programLabel({ state: "unsupported", version: null, update: null, detail: "no build" }), { key: "It cannot run there: {detail}", values: { detail: "no build" } });
  assert.deepEqual(programAction({ state: "missing", version: "2", update: null, detail: null }), { kind: "install" });
  assert.deepEqual(programAction({ state: "missing", version: "2", update: "2", detail: null }), { kind: "update", version: "2" });
  assert.deepEqual(programAction({ state: "runner", version: "1", update: "1.1", detail: "npx" }), { kind: "update", version: "1.1" });
  assert.equal(programAction({ state: "installed", version: "2", update: null, detail: null }), null);
  assert.equal(programAction(null), null);
});

test("a machine of an older version offers its types, each added through its form", () => {
  assert.deepEqual(offersOfTypes([{ type: "anthropic", name: "Anthropic", credential: "key", signIn: false }, { type: "copilot", name: "GitHub Copilot", credential: "sign_in", signIn: true },
    { type: "llama-cpp", name: "llama.cpp", credential: "none", signIn: false }]).map(value => [value.id, value.group, value.signIn]),
  [["anthropic", "key", false], ["copilot", "subscription", true], ["llama-cpp", "local", false]]);
});
