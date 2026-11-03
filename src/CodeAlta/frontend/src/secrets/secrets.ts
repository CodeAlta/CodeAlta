import type { MessageKey } from "../localization";

/** Whether a text can name a secret or a variable: the name of an environment variable (letters, digits, `_`, not a digit first). */
export function isSecretName(name: string): boolean {
  return /^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(name);
}

/** What is wrong with a name being typed, or null; an empty name is not wrong yet. */
export function secretNameProblem(name: string, taken: readonly string[]): MessageKey | null {
  const trimmed = name.trim();
  if (!trimmed) return null;
  if (!isSecretName(trimmed)) return "Letters, digits and _, not starting with a digit.";
  return taken.some(other => other.toLowerCase() === trimmed.toLowerCase()) ? "This name is already used." : null;
}

/** The name a secret that keeps the key of a provider is given first: `OPENAI_API_KEY` for `openai`. */
export function suggestedSecretName(providerKey: string): string {
  const base = providerKey.toUpperCase().replace(/[^A-Z0-9]+/g, "_").replace(/^_+|_+$/g, "");
  const name = `${base || "PROVIDER"}_API_KEY`;
  return /^[0-9]/.test(name) ? `_${name}` : name;
}

/** How the configuration refers to a secret or a variable. */
export function referenceOf(kind: "secret" | "var", name: string): string {
  return `\${${kind}:${name}}`;
}
