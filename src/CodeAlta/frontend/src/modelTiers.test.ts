import assert from "node:assert/strict";
import test from "node:test";
import type { GlobalConfigModelTiersResponse } from "#neoastra";
import { adaptiveChoice, adaptiveLevelOf, adaptiveLevelSummary, adaptiveTierOf, addTierTarget, composerAdaptiveEntry, composerTierEntries, readAdaptiveTier, createTierStore, emptyTiers, moveEffort, moveTierTarget, noTiers, readMaxResumes, readTierState,
  removeTierTarget, replaceTierTarget, setTierReasoning, startingEfforts, tierLists, type TierState } from "./modelTiers";

const reply = (changes: Partial<GlobalConfigModelTiersResponse> = {}): GlobalConfigModelTiersResponse => ({
  status: "ok", message: null, source: "global", spaceId: null, adaptive: null, adaptiveReady: false, adaptiveProposal: null, pending: false,
  fallback: { enabled: true, onQuota: true, onUnavailable: false, resume: false, maxResumes: 5 },
  tiers: [
    { tier: "complex", targets: [{ providerKey: "claude-code", modelId: "opus", reasoning: ["High", "XHigh"] }, { providerKey: "codex", modelId: "gpt-6.1", reasoning: ["XHigh"] }],
      choice: { providerKey: "codex", modelId: "gpt-6.1", reasoning: "XHigh", providerName: "Codex", modelName: "GPT-6.1" } },
    { tier: "standard", targets: [], choice: null },
    { tier: "quick", targets: [{ providerKey: "claude-code", modelId: "haiku", reasoning: [] }], choice: null },
  ],
  ...changes,
});

const state = () => readTierState(reply())!;

test("the tiers of the host are read with what each gives, and a reply that is not one is refused", () => {
  const read = state();
  assert.deepEqual(read.tiers.complex.map(target => target.modelId), ["opus", "gpt-6.1"]);
  assert.deepEqual(read.tiers.complex[0].reasoning, ["High", "XHigh"]);
  assert.equal(read.choices.complex?.modelName, "GPT-6.1");
  assert.equal(read.choices.quick, null);
  assert.deepEqual(read.fallback, { enabled: true, onQuota: true, onUnavailable: false, resume: false, maxResumes: 5 });
  assert.equal(noTiers(read), false);
  assert.equal(noTiers(emptyTiers), true);
  assert.equal(noTiers(null), true);
  assert.equal(readTierState(reply({ tiers: [{ tier: "fast", targets: [], choice: null }] })), null);
  assert.equal(readTierState(reply({ tiers: [{ tier: "quick", targets: [{ providerKey: "", modelId: "m", reasoning: [] }], choice: null }] })), null);
  assert.equal(readTierState({ tiers: [], fallback: null }), null);
  // What is saved is the tiers in order, without what they give.
  assert.deepEqual(tierLists(read).map(list => [list.tier, list.targets.length, list.choice]), [["complex", 2, null], ["standard", 0, null], ["quick", 1, null]]);
});

test("Adaptive is read with its levels, ready only when the host says so, and offered by the composer level by level", () => {
  const t = (key: string, values?: Record<string, string>) => key.replace(/\{(\w+)\}/g, (_, name: string) => values?.[name] ?? name);
  assert.equal(state().adaptive, null);
  assert.deepEqual(composerAdaptiveEntry(state(), t), { ready: false, detail: "Not set up", title: null, levels: [] });
  const sol = { providerKey: "codex", modelId: "gpt-6.1-sol", reasoning: "Low", providerName: "Codex", modelName: "GPT-6.1 Sol" };
  const levels = [
    { level: "max", model: { providerKey: "codex", modelId: "gpt-6.1-sol", reasoning: ["High"] },
      critics: [{ providerKey: "claude-code", modelId: "sonnet", reasoning: ["High"] }, { providerKey: "claude-code", modelId: "opus", reasoning: ["High"] }], choice: null, criticNames: null },
    { level: "medium", model: { providerKey: "codex", modelId: "gpt-6.1-sol", reasoning: ["Low"] }, critics: [{ providerKey: "claude-code", modelId: "sonnet", reasoning: ["High"] }], choice: sol, criticNames: ["Sonnet 5.5"] },
    { level: "huge", model: { providerKey: "codex", modelId: "gpt-6.1-sol", reasoning: [] }, critics: [], choice: null, criticNames: null },
  ];
  const ready = readTierState(reply({ adaptive: { level: "medium", levels }, adaptiveReady: true, adaptiveProposal: [levels[1]] }))!;
  // From the cheapest; a level that is none is left out.
  assert.deepEqual(ready.adaptive?.levels.map(level => level.level), ["medium", "max"]);
  assert.equal(ready.adaptive?.level, "medium");
  assert.deepEqual(ready.adaptiveProposal?.map(level => level.level), ["medium"]);
  assert.equal(adaptiveChoice(ready)?.modelId, "gpt-6.1-sol");
  // A level that is not set takes the nearest one, the cheaper on a tie, as the host does.
  assert.equal(adaptiveLevelOf(ready.adaptive, "low")?.level, "medium");
  assert.equal(adaptiveLevelOf(ready.adaptive, "high")?.level, "medium");
  assert.equal(adaptiveLevelOf(ready.adaptive, "max")?.level, "max");
  const entry = composerAdaptiveEntry(ready, t);
  assert.equal(entry.ready, true);
  assert.equal(entry.detail, "GPT-6.1 Sol Low · Review by Sonnet 5.5");
  // The provider of Max is not ready: it is offered without a model.
  assert.deepEqual(entry.levels.map(level => [level.level, level.detail]), [["medium", "GPT-6.1 Sol Low · Review by Sonnet 5.5"], ["max", null]]);
  assert.equal(adaptiveLevelSummary(ready.adaptive!.levels[1], t), "gpt-6.1-sol High · Review by sonnet, opus");
  // Set up, but the host says it has no level that can be chosen.
  const waiting = readTierState(reply({ adaptive: { level: "medium", levels: [] }, adaptiveReady: true }))!;
  assert.equal(waiting.adaptiveReady, false);
  assert.equal(adaptiveChoice(waiting), null);
  assert.equal(composerAdaptiveEntry(waiting, t).ready, false);
  // The draft of a new session names Adaptive at a level beside the tiers.
  assert.equal(adaptiveTierOf("high"), "adaptive:high");
  assert.equal(adaptiveTierOf(null), "adaptive");
  assert.deepEqual(readAdaptiveTier("adaptive:max"), { adaptive: true, level: "max" });
  assert.deepEqual(readAdaptiveTier("adaptive"), { adaptive: true, level: null });
  assert.deepEqual(readAdaptiveTier("adaptive:huge"), { adaptive: false, level: null });
  assert.deepEqual(readAdaptiveTier("quick"), { adaptive: false, level: null });
});

test("the resume after a limit is read with its number of resumes in a row, the default for one the host would refuse", () => {
  const read = readTierState(reply({ fallback: { enabled: true, onQuota: true, onUnavailable: true, resume: true, maxResumes: 12 } }))!;
  assert.deepEqual(read.fallback, { enabled: true, onQuota: true, onUnavailable: true, resume: true, maxResumes: 12 });
  assert.equal(emptyTiers.fallback.resume, false);
  assert.equal(emptyTiers.fallback.maxResumes, 5);
  assert.deepEqual([readMaxResumes(1), readMaxResumes(50), readMaxResumes(0), readMaxResumes(51), readMaxResumes(2.5), readMaxResumes("3"), readMaxResumes(undefined)],
    [1, 50, 5, 5, 5, 5, 5]);
});

test("a model is added once to a tier, removed, and given its efforts in order", () => {
  const added = addTierTarget(state(), "standard", { providerKey: "codex", modelId: "gpt-6.1", reasoning: ["Medium"] })!;
  assert.deepEqual(added.tiers.standard.map(target => target.modelId), ["gpt-6.1"]);
  assert.equal(addTierTarget(added, "standard", { providerKey: "CODEX", modelId: "GPT-6.1", reasoning: [] }), null, "The same model of the same provider is once in a tier.");
  assert.ok(addTierTarget(added, "quick", { providerKey: "codex", modelId: "gpt-6.1", reasoning: [] }), "Another tier may have it.");
  assert.deepEqual(removeTierTarget(added, "complex", 0).tiers.complex.map(target => target.modelId), ["gpt-6.1"]);
  assert.deepEqual(setTierReasoning(added, "complex", 0, ["XHigh", "High", "XHigh"]).tiers.complex[0].reasoning, ["XHigh", "High"]);
});

test("a target moves in its tier and into another one, never twice into the same tier", () => {
  const read = state();
  assert.deepEqual(moveTierTarget(read, { tier: "complex", index: 1 }, { tier: "complex", index: 0 })!.tiers.complex.map(target => target.modelId), ["gpt-6.1", "opus"]);
  assert.deepEqual(moveTierTarget(read, { tier: "complex", index: 0 }, { tier: "complex", index: 2 })!.tiers.complex.map(target => target.modelId), ["gpt-6.1", "opus"]);
  assert.equal(moveTierTarget(read, { tier: "complex", index: 0 }, { tier: "complex", index: 1 }), null, "A move to where it is changes nothing.");
  const across = moveTierTarget(read, { tier: "complex", index: 0 }, { tier: "quick", index: 0 })!;
  assert.deepEqual(across.tiers.quick.map(target => target.modelId), ["opus", "haiku"]);
  assert.deepEqual(across.tiers.complex.map(target => target.modelId), ["gpt-6.1"]);
  const twice: TierState = addTierTarget(read, "quick", { providerKey: "codex", modelId: "gpt-6.1", reasoning: [] })!;
  assert.equal(moveTierTarget(twice, { tier: "complex", index: 1 }, { tier: "quick", index: 0 }), null);
});

test("a target changes its model in its place, with the efforts the new model offers", () => {
  const read = state();
  // The efforts the new model offers stay, in the order of the target, spelled as the model names them.
  const changed = replaceTierTarget(read, "complex", 0, { providerKey: "codex", modelId: "gpt-6.2" }, { efforts: ["low", "xhigh", "high"], defaultEffort: "low" })!;
  assert.deepEqual(changed.tiers.complex.map(target => `${target.providerKey}:${target.modelId}`), ["codex:gpt-6.2", "codex:gpt-6.1"]);
  assert.deepEqual(changed.tiers.complex[0].reasoning, ["high", "xhigh"]);
  assert.equal(read.tiers.complex[0].modelId, "opus", "The tiers read are left as they are.");
  // A model that offers none of them starts with its own effort; one without efforts with none.
  assert.deepEqual(replaceTierTarget(read, "complex", 0, { providerKey: "claude-code", modelId: "sonnet" }, { efforts: ["Low", "Medium"], defaultEffort: "Medium" })!.tiers.complex[0].reasoning, ["Medium"]);
  assert.deepEqual(replaceTierTarget(read, "complex", 0, { providerKey: "local", modelId: "small" }, { efforts: [], defaultEffort: null })!.tiers.complex[0].reasoning, []);
  // A model the tier has already is refused, the target's own included; so is an index without a target.
  assert.equal(replaceTierTarget(read, "complex", 0, { providerKey: "CODEX", modelId: "GPT-6.1" }, { efforts: ["XHigh"], defaultEffort: null }), null);
  assert.equal(replaceTierTarget(read, "complex", 0, { providerKey: "claude-code", modelId: "opus" }, { efforts: ["High"], defaultEffort: null }), null);
  assert.equal(replaceTierTarget(read, "standard", 0, { providerKey: "codex", modelId: "gpt-6.2" }, undefined), null);
  // Another tier may have it.
  assert.ok(replaceTierTarget(read, "quick", 0, { providerKey: "claude-code", modelId: "opus" }, { efforts: ["High"], defaultEffort: null }));
});

test("efforts move before another one, and a model starts with its default effort", () => {
  assert.deepEqual(moveEffort(["High", "XHigh", "Medium"], 2, 0), ["Medium", "High", "XHigh"]);
  assert.deepEqual(moveEffort(["High", "XHigh", "Medium"], 0, 3), ["XHigh", "Medium", "High"]);
  assert.deepEqual(startingEfforts({ efforts: ["Low", "Medium", "High"], defaultEffort: "Medium" }), ["Medium"]);
  assert.deepEqual(startingEfforts({ efforts: ["Low", "High"], defaultEffort: null }), ["High"]);
  assert.deepEqual(startingEfforts({ efforts: ["Low"], defaultEffort: null }), ["Low"]);
  assert.deepEqual(startingEfforts({ efforts: [], defaultEffort: null }), []);
  assert.deepEqual(startingEfforts(undefined), []);
});

test("the composer offers the tiers that have models, with the model each gives, and none without tiers", () => {
  const entries = composerTierEntries(state(), key => key)!;
  assert.deepEqual(entries.map(entry => [entry.tier, entry.label, entry.detail]), [["complex", "Complex", "GPT-6.1 · XHigh"], ["quick", "Quick", null]]);
  assert.equal(entries[0].title, "Codex · GPT-6.1 · XHigh");
  assert.equal(composerTierEntries(emptyTiers, key => key), undefined);
  assert.equal(composerTierEntries(null, key => key), undefined);
});

test("the store reads the tiers of a host once at a time and tells its listeners", async () => {
  let reads = 0;
  const store = createTierStore(async request => { reads++; return { ...reply(), status: request.expectedEpoch === "bad" ? "stale_epoch" : "ok" }; });
  let told = 0;
  const stop = store.subscribe(() => told++);
  await Promise.all([store.refresh("e1"), store.refresh("e1")]);
  assert.equal(reads, 1);
  assert.equal(told, 1);
  assert.ok(store.of("e1"));
  await store.refresh("bad");
  assert.equal(store.of("bad"), undefined, "A reading that is not ok gives none.");
  store.set("e1", emptyTiers);
  assert.equal(told, 2);
  stop();
  store.set("e1", state());
  assert.equal(told, 2);
});

test("a reading made while a provider of the tiers is prepared is made again until the tiers give their models (#367)", async () => {
  let starting = true;
  const asked: string[] = [];
  const planned: (() => void)[] = [];
  const store = createTierStore(async request => {
    asked.push(request.spaceId ?? "");
    return starting ? reply({ pending: true, tiers: [{ tier: "standard", targets: [{ providerKey: "custom", modelId: "glm", reasoning: [] }], choice: null }] })
      : reply({ pending: false, tiers: [{ tier: "standard", targets: [{ providerKey: "custom", modelId: "glm", reasoning: [] }],
        choice: { providerKey: "custom", modelId: "glm", reasoning: null, providerName: "Custom", modelName: "GLM" } }] });
  }, { delay: 10, reads: 3, schedule: run => { planned.push(run); } });

  await store.refresh("e1", { spaceId: "work" });
  assert.equal(store.of("e1", { spaceId: "work" })?.choices.standard, null);
  assert.equal(planned.length, 1, "The reading is planned again.");
  await store.refresh("e1", { spaceId: "work" });
  assert.equal(planned.length, 1, "Once at a time.");

  starting = false;
  planned.shift()!();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(store.of("e1", { spaceId: "work" })?.choices.standard?.modelName, "GLM");
  assert.equal(planned.length, 0, "A reading that is not pending is not made again.");

  // A provider that never gets ready is read again a bounded number of times.
  assert.deepEqual(asked, ["work", "work", "work"]);
  let bounded = 0;
  const never = createTierStore(async () => { bounded++; return reply({ pending: true }); }, { delay: 10, reads: 2, schedule: run => { planned.push(run); } });
  await never.refresh("e1");
  while (planned.length > 0) { planned.shift()!(); await new Promise(resolve => setImmediate(resolve)); }
  assert.equal(bounded, 3, "The first reading and two more.");

  // A reading again that fails (the host is busy) is tried again while the providers are prepared.
  let attempt = 0;
  const flaky = createTierStore(async () => { attempt++; if (attempt === 2) throw new Error("timeout"); return reply({ pending: attempt < 3 }); },
    { delay: 10, reads: 5, schedule: run => { planned.push(run); } });
  await flaky.refresh("e1");
  while (planned.length > 0) { planned.shift()!(); await new Promise(resolve => setImmediate(resolve)); }
  assert.equal(attempt, 3, "Pending, failed, then read.");
});

test("a change of the providers reads again every scope of the host the window knows", async () => {
  const asked: string[] = [];
  const store = createTierStore(async request => { asked.push(`${request.expectedEpoch}|${request.spaceId ?? ""}`); return reply(); });
  await store.refresh("e1");
  await store.refresh("e1", { spaceId: "work" });
  await store.refresh("e0", { spaceId: "work" });
  asked.length = 0;
  await store.refreshAll("e1");
  assert.deepEqual(asked.sort(), ["e1|", "e1|work"], "Only the scopes of that host are read again.");
});

test("a reading that says what the window already has tells nobody, and keeps the state a composer holds", async () => {
  const store = createTierStore(async () => reply());
  await store.refresh("e1");
  const first = store.of("e1");
  const readings = store.get();
  let told = 0;
  const stop = store.subscribe(() => told++);
  // Each composer reads its scope again when its session is shown.
  await store.refresh("e1");
  await store.refresh("e1");
  assert.equal(told, 0);
  assert.equal(store.of("e1"), first);
  assert.equal(store.get(), readings, "the window renders again for a change only");
  store.set("e1", { ...first!, fallback: { ...first!.fallback, enabled: false } });
  assert.equal(told, 1);
  stop();
});
