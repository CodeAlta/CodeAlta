import assert from "node:assert/strict";
import test from "node:test";
import type { SkillsEntry } from "#neoastra";
import { searchSkills, skillCall, skillPrompt, usableSkills } from "./skillResults";

const entry = (name: string, values: Partial<SkillsEntry> = {}): SkillsEntry => ({ id: name, name, title: name, description: `The ${name} skill.`, enabled: true,
  enabledGlobal: true, enabledProject: true, folder: null, path: null, scope: "Global", shadowed: false, source: "UserAlta", trusted: true, valid: true, ...values });

test("the search offers the skills a session is given: enabled, valid, trusted and not overridden, by name", () => {
  const skills = usableSkills([entry("pickup"), entry("handoff"), entry("off", { enabled: false }), entry("broken", { valid: false }),
    entry("foreign", { trusted: false }), entry("hidden", { shadowed: true }), entry("Handoff", { source: "ProjectAlta" })]);
  assert.deepEqual(skills.map(skill => skill.name), ["handoff", "pickup"], "a name is offered once, as the list gives it first");
});

test("a skill is found by its name first, then its title, then its description", () => {
  const skills = usableSkills([entry("handoff", { description: "Summarize the conversation into a file." }), entry("pickup", { description: "Read a handoff file." }),
    entry("review", { title: "Code review" })]);
  assert.deepEqual(searchSkills(skills, "/hand").map(result => result.skill.name), ["handoff", "pickup"]);
  assert.deepEqual(searchSkills(skills, "code").map(result => result.skill.name), ["review"]);
  assert.deepEqual(searchSkills(skills, "/").map(result => result.skill.name), ["handoff", "pickup", "review"], "after a slash alone, every skill");
  assert.deepEqual(searchSkills(skills, "/zzz"), []);
});

test("what follows the whole name of a skill goes with it to the session", () => {
  const skills = usableSkills([entry("handoff"), entry("handoff-full")]);
  const [found, ...others] = searchSkills(skills, "/Handoff  to the Review session ");
  assert.equal(others.length, 0, "the named skill alone");
  assert.equal(found.skill.name, "handoff");
  assert.equal(found.args, "to the Review session", "as it was typed");
  assert.deepEqual(skillCall("/handoff"), { name: "handoff", args: "" });
  assert.equal(skillCall("handoff now"), null);
  assert.deepEqual(searchSkills(skills, "/handoff").map(result => [result.skill.name, result.args]), [["handoff", ""], ["handoff-full", ""]]);
});

test("the prompt asks the agent to activate the skill and follow it, with what the user added", () => {
  assert.equal(skillPrompt("handoff", ""), "Activate the skill `handoff` and follow its instructions.");
  assert.equal(skillPrompt("handoff", " for the review "), "Activate the skill `handoff` and follow its instructions.\n\nfor the review");
});
