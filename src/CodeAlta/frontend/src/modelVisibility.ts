// The rules that hide some models of a provider (`models_hide`) and show some again (`models_show`), as the page of the
// provider edits them and the configuration file writes them: `regex:<pattern>`, `exact:<id>`, or an id alone, matched
// whatever its case. They are matched on the host only (CodeAlta.Agent.ModelVisibility), with .NET regular expressions.

/** How a rule matches the id of a model. */
export type ModelRuleMatch = "any-case" | "exact" | "regex";

/** A rule of the form: how it matches, and the id or the regular expression. */
export type ModelRuleDraft = Readonly<{ match: ModelRuleMatch; pattern: string }>;

const regexPrefix = "regex:";
const exactPrefix = "exact:";

/** Reads a rule as the configuration file writes it. */
export function parseModelRule(text: string): ModelRuleDraft {
  const trimmed = text.trim();
  const lower = trimmed.toLowerCase();
  if (lower.startsWith(regexPrefix)) return { match: "regex", pattern: trimmed.slice(regexPrefix.length).trim() };
  if (lower.startsWith(exactPrefix)) return { match: "exact", pattern: trimmed.slice(exactPrefix.length).trim() };
  return { match: "any-case", pattern: trimmed };
}

/** Writes a rule as the configuration file keeps it; an empty one is left out (null). */
export function formatModelRule(rule: ModelRuleDraft): string | null {
  const pattern = rule.pattern.trim();
  if (!pattern) return null;
  return rule.match === "regex" ? regexPrefix + pattern : rule.match === "exact" ? exactPrefix + pattern : pattern;
}

/** The rules of a list as the configuration keeps them, the empty ones left out. */
export function formatModelRules(rules: readonly ModelRuleDraft[]): string[] {
  return rules.map(formatModelRule).filter((rule): rule is string => rule !== null);
}
