import { fieldsScore, readQuery } from "./searchResults";

/** A skill the search offers: one a prompt of a session of the project in front can name, as the host lists them (`skills.offered`). */
export type SkillChoice = Readonly<{ name: string; title: string; description: string; source: string }>;

/** A skill the search found, with the text typed after its name, which goes with it to the session. */
export type SkillResult = Readonly<{ kind: "skill"; key: string; score: number; skill: SkillChoice; args: string }>;

/** A text typed as `/name what to do`: the name and what follows it, as it was typed. Null for any other text. */
export function skillCall(text: string): Readonly<{ name: string; args: string }> | null {
  const match = /^\s*\/(\S+)(?:\s+([\s\S]*))?$/u.exec(text);
  return match ? { name: match[1], args: (match[2] ?? "").trim() } : null;
}

/**
 * The skills a query finds: by their name first, then by their title, then by their description. A query that
 * names a skill whole and goes on (`/handoff to the review session`) finds that skill alone, with the rest of the
 * text as what to do with it.
 */
export function searchSkills(skills: readonly SkillChoice[], text: string): SkillResult[] {
  const call = skillCall(text);
  if (call?.args) {
    const named = skills.find(skill => skill.name.toLowerCase() === call.name.toLowerCase());
    if (named) return [{ kind: "skill", key: `skill:${named.name}`, score: 0, skill: named, args: call.args }];
  }
  const { words } = readQuery(text);
  const results: SkillResult[] = [];
  for (const skill of skills) {
    const score = fieldsScore(words, [[skill.name, 0], [skill.title, 5], [skill.description, 300]]);
    if (score !== null) results.push({ kind: "skill", key: `skill:${skill.name}`, score, skill, args: "" });
  }
  return results.sort((left, right) => left.score - right.score || left.skill.name.localeCompare(right.skill.name, undefined, { sensitivity: "base" }));
}
