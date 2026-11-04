import assert from "node:assert/strict";
import test from "node:test";
import type { FleetServerView } from "#neoastra";
import { bannerOf, draftOf, minutesOf, ruleCount, rulesOf, sameDraft, splitList, stopReady, stopSentence } from "./policies";

test("a form of rules says what it holds, and nothing for what it leaves empty", () => {
  const empty = draftOf(null);
  assert.deepEqual(rulesOf(empty), { providers: null, models: null, sandbox: null, toolsOff: null, network: null, remoteStarts: null, agentStarts: null, maxSessionMinutes: null,
    permissionMode: null, publishFacts: null, accountCredentials: null });
  const rules = rulesOf({ ...empty, providers: "anthropic, openai,, anthropic", models: "claude-*\ngpt-6.1", agentStarts: "no", maxSessionMinutes: " 90 ", publishFacts: true });
  assert.deepEqual(rules?.providers, ["anthropic", "openai"]);
  assert.deepEqual(rules?.models, ["claude-*", "gpt-6.1"]);
  assert.equal(rules?.agentStarts, false);
  assert.equal(rules?.remoteStarts, null, "allowed says nothing: a rule only narrows");
  assert.equal(rules?.maxSessionMinutes, 90);
  assert.equal(rules?.publishFacts, true);
  assert.equal(rulesOf({ ...empty, maxSessionMinutes: "forever" }), null, "a longest time that does not read holds no rules");
  assert.deepEqual(draftOf(rules), { ...empty, providers: "anthropic, openai", models: "claude-*, gpt-6.1", agentStarts: "no", maxSessionMinutes: "90", publishFacts: true });
  assert.ok(sameDraft(draftOf(rules), { ...draftOf(rules), providers: "anthropic,openai" }), "the same rules, written otherwise");
  assert.equal(ruleCount(rules), 5);
});

test("lists, minutes and stops", () => {
  assert.deepEqual(splitList(" a ,b\n\n a "), ["a", "b"]);
  assert.equal(splitList("  "), null);
  assert.equal(minutesOf(""), null);
  assert.equal(minutesOf("0"), undefined);
  assert.equal(minutesOf(String(7 * 24 * 60 + 1)), undefined);
  assert.equal(stopReady("fleet", [], []), true);
  assert.equal(stopReady("machines", [], []), false);
  assert.equal(stopReady("teams", [], ["infra"]), true);
  assert.deepEqual(stopSentence({ scope: "machines", names: ["Build box", "Mac mini"] }), { key: "The work of the fleet is stopped on {names}.", values: { names: "Build box, Mac mini" } });
});

test("the window shows the first stop a server holds", () => {
  const server = (id: string, stop: FleetServerView["stop"]): FleetServerView => ({ id, url: `https://${id}.example`, organization: id, link: "approved", connection: "online", problem: null,
    controller: true, spaces: [], recoveryKey: null, stop });
  assert.equal(bannerOf([server("home", null)]), null);
  const banner = bannerOf([server("home", null), server("work", { scope: "machines", reason: "Runaway job", machines: 2, teams: 0, since: null, number: 4 })]);
  assert.deepEqual([banner?.server.id, banner?.label, banner?.values], ["work", "Fleet stopped: {count} machines", { count: 2 }]);
  assert.equal(bannerOf([server("home", { scope: "fleet", reason: null, machines: 0, teams: 0, since: null, number: 1 })])?.label, "Fleet stopped: every machine");
});
