/**
 * A prompt one agent session delivered to another: a sub-session reporting to its parent, or a session
 * writing to a peer through the alta tool. The host records it as a user prompt with a routing envelope.
 */
export type DelegatedMessage = Readonly<{ sourceSessionId: string | null; kind: string; body: string }>;

const marker = "[CodeAlta delegated-agent message]";

/** Reads the envelope of a delegated prompt, or returns null for any other text. */
export function parseDelegatedMessage(text: string | null | undefined): DelegatedMessage | null {
  if (!text?.startsWith(marker)) return null;
  const lines = text.split(/\r?\n/);
  const fields = new Map<string, string>();
  let index = 1;
  for (; index < lines.length && lines[index].trim() !== ""; index++) {
    const split = lines[index].indexOf(": ");
    if (split < 1) return null;
    fields.set(lines[index].slice(0, split), lines[index].slice(split + 2).trim());
  }
  const source = fields.get("Source session");
  const kind = fields.get("Kind");
  if (!source || !kind) return null;
  index++;
  // A sub-session update names its run and content before the text itself.
  if (/^\[CodeAlta child-session .+ update\]$/.test(lines[index]?.trim() ?? "")) {
    while (index < lines.length && lines[index].trim() !== "") index++;
    index++;
  }
  return { sourceSessionId: source === "unknown" ? null : source, kind, body: lines.slice(index).join("\n").trim() };
}
