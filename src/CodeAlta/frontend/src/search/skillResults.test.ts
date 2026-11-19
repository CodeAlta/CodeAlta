import assert from "node:assert/strict";
import test from "node:test";
import { searchSkills, skillCall, type SkillChoice } from "./skillResults";

const entry = (name: string, values: Partial<SkillChoice> = {}): SkillChoice => ({ name, title: name, description: `The ${name} skill.`, source: "UserAlta", ...values });

test("a skill is found by its name first, then its title, then its description", () => {
  const skills = [entry("handoff", { description: "Summarize the conversation into a file." }), entry("pickup", { description: "Read a handoff file." }),
    entry("review", { title: "Code review" })];
  assert.deepEqual(searchSkills(skills, "/hand").map(result => result.skill.name), ["handoff", "pickup"]);
  assert.deepEqual(searchSkills(skills, "code").map(result => result.skill.name), ["review"]);
  assert.deepEqual(searchSkills(skills, "/").map(result => result.skill.name), ["handoff", "pickup", "review"], "after a slash alone, every skill");
  assert.deepEqual(searchSkills(skills, "/zzz"), []);
});

test("what follows the whole name of a skill goes with it to the session", () => {
  const skills = [entry("handoff"), entry("handoff-full")];
  const [found, ...others] = searchSkills(skills, "/Handoff  to the Review session ");
  assert.equal(others.length, 0, "the named skill alone");
  assert.equal(found.skill.name, "handoff");
  assert.equal(found.args, "to the Review session", "as it was typed");
  assert.deepEqual(skillCall("/handoff"), { name: "handoff", args: "" });
  assert.equal(skillCall("handoff now"), null);
  assert.deepEqual(searchSkills(skills, "/handoff").map(result => [result.skill.name, result.args]), [["handoff", ""], ["handoff-full", ""]]);
});
