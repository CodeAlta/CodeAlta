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
 * Submit advances before it submits: with questions the user has not looked at yet it goes to the first
 * of them, and only once every question was shown does it send the answer.
 */
export function submitStep(visited: ReadonlySet<number>, current: number, count: number): { kind: "submit" } | { kind: "show"; index: number } {
  for (let index = 0; index < count; index++) if (index !== current && !visited.has(index)) return { kind: "show", index };
  return { kind: "submit" };
}
