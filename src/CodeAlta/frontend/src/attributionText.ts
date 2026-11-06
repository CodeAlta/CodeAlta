import { useEffect, useState } from "react";

/** What is saved of an attribution text: the text on one line, or empty for the text of CodeAlta, also when it is that text. */
export function attributionSaved(draft: string, defaultValue: string): string {
  const text = draft.replace(/\s+/g, " ").trim();
  return text === defaultValue ? "" : text;
}

/**
 * The draft of an attribution text: it starts as the text agents get, the one of CodeAlta until the user writes another,
 * so that the user tweaks it rather than writing it again. `value` is that text, as the host reads it.
 */
export function useAttributionDraft(value: string, defaultValue: string, onSave: (text: string) => void) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  // What the configuration file holds: empty while the text is the one of CodeAlta.
  const stored = value === defaultValue ? "" : value;
  const pending = attributionSaved(draft, defaultValue) !== stored;
  return {
    draft, setDraft, pending,
    /** Whether the text is one the user wrote, which the text of CodeAlta can replace. */
    own: stored !== "",
    save: () => { const text = attributionSaved(draft, defaultValue); if (text !== stored) onSave(text); },
    /** Goes back to the text of CodeAlta at once. */
    restore: () => { setDraft(defaultValue); if (stored !== "") onSave(""); },
  };
}
