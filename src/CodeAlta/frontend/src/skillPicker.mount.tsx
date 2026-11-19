// Disposable page: a prompt (a text area) with the `/` skill picker of the composers and three skills. Driven by
// skillPicker.browser.test.ts through the keyboard; `#refreshes` counts the times the picker asked for the skills again.
// The text area holds its own text, as the editor of the composer does: the picker writes in it, React does not.
import { StrictMode, useMemo, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { SkillPromptPicker } from "./SkillPromptPicker";
import { SkillChoicesContext, type SkillChoices } from "./skillMentions";
import type { PromptInput } from "./PromptEditor";
import { dismissDialogsOnOutsidePress } from "./modalDialogs";

const skills = [
  { name: "grilling", title: "Grilling", description: "Grill the user about a plan.", source: "UserCommon" },
  { name: "grill-me", title: "Grill me", description: "A relentless interview.", source: "UserCommon" },
  { name: "tdd", title: "TDD", description: "Test-driven development.", source: "Builtin" },
];

document.documentElement.classList.add("bp6-dark");
// As the application does: a press outside a modal window dismisses it.
dismissDialogsOnOutsidePress(document);

function Fixture() {
  const input = useRef<HTMLTextAreaElement>(null);
  const [refreshes, setRefreshes] = useState(0);
  const choices = useMemo<SkillChoices>(() => ({ skills, refresh: () => setRefreshes(value => value + 1), mentions: async () => [],
    // The host of the fixture: a `/` after a backtick left open is in code; it answers after a moment, as a host does.
    canBegin: (text, at) => new Promise(resolve => setTimeout(() => resolve(text.slice(0, at).split("`").length % 2 === 1), 30)) }), []);
  return <SkillChoicesContext.Provider value={choices}>
    <output id="refreshes">{refreshes}</output>
    <textarea id="prompt" ref={input} />
    <SkillPromptPicker edit={text => { if (input.current) input.current.value = text; }} input={input as React.RefObject<PromptInput | null>} />
  </SkillChoicesContext.Provider>;
}
// Under StrictMode, as in the application: React then runs each effect of a new component twice.
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
