import type { AskQuestion } from "./sessionAsks";

/** A question's tab: an arrow while more questions follow, a check on the last one. */
export function questionTabTitle(title: string, index: number, count: number): string {
  return `${title} ${index === count - 1 ? "✓" : "→"}`;
}

/**
 * The choice an answer carries: the one the user picked, or the first when the question has choices and
 * none was picked yet (a choice question always answers with a choice).
 */
export function selectedChoice(question: Pick<AskQuestion, "choices">, chosen: readonly number[] | undefined): number | null {
  if (question.choices.length === 0) return null;
  const picked = chosen?.find(index => Number.isInteger(index) && index >= 0 && index < question.choices.length);
  return picked ?? 0;
}

/**
 * The choice indexes an answer carries: all valid selected choices (sorted, deduplicated) for a multi-select question,
 * or the single picked/default choice for a single-select question.
 */
export function selectedChoices(question: Pick<AskQuestion, "choices" | "allowMultiSelect">, chosen: readonly number[] | undefined): number[] {
  if (question.choices.length === 0) return [];
  if (question.allowMultiSelect) {
    const valid = new Set<number>();
    for (const index of chosen ?? []) {
      if (Number.isInteger(index) && index >= 0 && index < question.choices.length) valid.add(index);
    }
    return [...valid].sort((a, b) => a - b);
  }
  const single = selectedChoice(question, chosen);
  return single === null ? [] : [single];
}

/** Updates the selected choices when the user picks or toggles an option. */
export function toggleChoice(chosen: readonly number[] | undefined, value: number, multiSelect: boolean): number[] {
  if (!multiSelect) return [value];
  const next = new Set(chosen ?? []);
  if (next.has(value)) next.delete(value); else next.add(value);
  return [...next].sort((a, b) => a - b);
}

/**
 * Submit advances before it submits: with questions the user has not looked at yet it goes to the first
 * of them, and only once every question was shown does it send the answer.
 */
export function submitStep(visited: ReadonlySet<number>, current: number, count: number): { kind: "submit" } | { kind: "show"; index: number } {
  for (let index = 0; index < count; index++) if (index !== current && !visited.has(index)) return { kind: "show", index };
  return { kind: "submit" };
}
