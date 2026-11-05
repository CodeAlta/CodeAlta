import assert from "node:assert/strict";
import test from "node:test";
import { formatModelRule, formatModelRules, parseModelRule } from "./modelVisibility";

test("a rule reads and writes as the configuration file keeps it", () => {
  assert.deepEqual(parseModelRule("  gpt-5.4 "), { match: "any-case", pattern: "gpt-5.4" });
  assert.deepEqual(parseModelRule("exact:GPT-5.4"), { match: "exact", pattern: "GPT-5.4" });
  assert.deepEqual(parseModelRule("Regex: ^claude-"), { match: "regex", pattern: "^claude-" });
  assert.deepEqual(parseModelRule("regex:(?i:^grok)"), { match: "regex", pattern: "(?i:^grok)" }, "a pattern is kept as .NET reads it");
  assert.equal(formatModelRule({ match: "regex", pattern: " ^claude- " }), "regex:^claude-");
  assert.equal(formatModelRule({ match: "any-case", pattern: "  " }), null);
  assert.deepEqual(formatModelRules([{ match: "exact", pattern: "a" }, { match: "regex", pattern: "" }, { match: "any-case", pattern: "b" }]), ["exact:a", "b"]);
  for (const text of ["gpt-5.4", "exact:Composer", "regex:\\Aclaude-"]) assert.equal(formatModelRule(parseModelRule(text)), text);
});
