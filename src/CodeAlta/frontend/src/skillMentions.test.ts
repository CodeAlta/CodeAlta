import assert from "node:assert/strict";
import test from "node:test";
import { activeSkillReference, insertSkillCall, skillTokenAllowed, skillCallAtCaret, skillMentionSpans, skillNames, typedSkillQuery } from "./skillMentions";

const known = skillNames([{ name: "grilling", title: "", description: "", source: "UserCommon" }, { name: "code-review", title: "", description: "", source: "Builtin" }]);

test("/ opens the skill picker at a word start and carries the typed query", () => {
  assert.deepEqual(activeSkillReference("ask /", 5), { start: 4, end: 5, query: "" });
  assert.deepEqual(activeSkillReference("ask /gri", 8), { start: 4, end: 8, query: "gri" });
  assert.deepEqual(activeSkillReference("(/gri", 5), { start: 1, end: 5, query: "gri" });
  // The caret in the middle of a token replaces the whole token.
  assert.deepEqual(activeSkillReference("/grilling now", 3), { start: 0, end: 9, query: "gr" });
  assert.equal(activeSkillReference("src/app", 7), null);
  assert.equal(activeSkillReference("a //", 4), null);
  assert.equal(activeSkillReference("/usr/bin", 8), null);
  // The punctuation after a token is not of it: a skill chosen in its place keeps the period.
  assert.deepEqual(activeSkillReference("ask /tdd. Then", 6), { start: 4, end: 8, query: "t" });
  assert.deepEqual(activeSkillReference("(/tdd: \"x\")", 3), { start: 1, end: 5, query: "t" });
  // A `/` typed before a word starts a token of its own: choosing a skill keeps the word.
  assert.deepEqual(activeSkillReference("please review /this", 15), { start: 14, end: 15, query: "" });
});

test("what was typed after a `/` goes back in the prompt when no skill is chosen", () => {
  assert.deepEqual(typedSkillQuery("look at /", 8, 9, "usr/"), { text: "look at /usr/", caret: 13 });
  assert.deepEqual(typedSkillQuery("a /x b", 2, 4, "tmp "), { text: "a /tmp  b", caret: 7 });
  assert.equal(typedSkillQuery("a", 2, 2, "x"), null);
});

test("a skill chosen in the search goes at the caret, or at the end without the editor", () => {
  assert.deepEqual(skillCallAtCaret(null, "data", "/grilling my question"), { text: "data /grilling my question ", caret: 27 });
  assert.equal(skillCallAtCaret(null, "data", "grilling"), null);
});

test("a chosen skill is written as /name, apart from the word before it, with a space where typing goes on", () => {
  assert.deepEqual(insertSkillCall("ask /gri", 4, 8, "grilling"), { text: "ask /grilling ", caret: 14 });
  assert.deepEqual(insertSkillCall("", 0, 0, "grilling"), { text: "/grilling ", caret: 10 });
  assert.deepEqual(insertSkillCall("/gri then", 0, 4, "grilling"), { text: "/grilling then", caret: 10 });
  assert.deepEqual(insertSkillCall("data", 4, 4, "grilling"), { text: "data /grilling ", caret: 15 });
  assert.deepEqual(insertSkillCall("", 0, 0, "grilling", " my question "), { text: "/grilling my question ", caret: 22 });
  assert.deepEqual(insertSkillCall("ask /td. Then", 4, 7, "tdd"), { text: "ask /tdd. Then", caret: 8 });
  assert.deepEqual(insertSkillCall("(/gr)", 1, 4, "grilling"), { text: "(/grilling)", caret: 10 });
  assert.equal(insertSkillCall("", 0, 0, ""), null);
  assert.equal(insertSkillCall("", 0, 0, "two words"), null);
});

test("the /name of known skills are found, as the host gives them to the model", () => {
  assert.deepEqual(skillMentionSpans("/grilling then /tdd and (/Code-Review).", known),
    [{ start: 0, end: 9, name: "grilling" }, { start: 25, end: 37, name: "Code-Review" }]);
  assert.deepEqual(skillMentionSpans("src/grilling and /grilling/x and https://a/grilling", known), []);
  assert.deepEqual(skillMentionSpans("/grilling", new Set()), []);
  // As the host: names of any script, beyond the first plane too, and the white space of .NET.
  const others = new Set(["\u{10428}\u{10429}", "tdd"]);
  assert.deepEqual(skillMentionSpans("/\u{10428}\u{10429} ask\u0085/tdd and\u00a0/tdd", others).map(span => span.name), ["\u{10428}\u{10429}", "tdd", "tdd"]);
  assert.deepEqual(skillMentionSpans("\ufeff/tdd /tdd\u00b2 /tdd-", others), []);
});


test("the host is asked whether a / may open the picker only where Markdown could make it code", async () => {
  const asked: [string, number][] = [];
  const choices = { skills: [], refresh: () => { }, mentions: async () => [], canBegin: async (text: string, at: number) => { asked.push([text, at]); return false; } };
  assert.equal(skillTokenAllowed(choices, "ask /", 4), true);
  assert.equal(skillTokenAllowed(null, "ask /", 4), false);
  for (const text of ["run `cd /", "~~~\ncd /", "    cd /", "\tcd /", "<div> /", "a \\ /", "[x](/"]) assert.equal(await skillTokenAllowed(choices, text, text.length - 1), false, text);
  assert.equal(asked.length, 7);
  // What follows the `/` does not matter: it is not typed yet, or it is what the picker replaces.
  assert.equal(skillTokenAllowed(choices, "ask / `later`", 4), true);
});
