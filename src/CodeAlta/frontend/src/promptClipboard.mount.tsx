// Test-owned page: the production prompt editor with a `#` picker of the prompt, the modal window the composers open
// over it. Driven by promptClipboard.browser.test.ts through `window.clipboardFixture`.
import { useRef, useState, type RefObject } from "react";
import { createRoot } from "react-dom/client";
import { activeIssueReference } from "./issueReferences";
import { PromptEditor, type PromptInput } from "./PromptEditor";
import { usePromptPicker } from "./promptPicker";

function Picker({ input, edit }: { input: RefObject<PromptInput | null>; edit: (text: string) => void }) {
  const search = useRef<HTMLInputElement>(null);
  const { trigger, dialog, close } = usePromptPicker({ input, edit, enabled: true, detect: activeIssueReference, focus: search, onOpen: () => { } });
  return trigger && <dialog ref={dialog} className="reference-palette" onCancel={event => { event.preventDefault(); close(); }}>
    <input ref={search} type="search" aria-label="Search issues" />
  </dialog>;
}

const state = { value: "" };
let input: PromptInput | null = null;
function Fixture() {
  const editor = useRef<PromptInput | null>(null);
  const [value, setValue] = useState("");
  state.value = value;
  // As in the composer: the picker sits in the toolbar under the prompt, beside a text read to screen readers.
  return <>
    <PromptEditor ref={instance => { editor.current = instance; input = instance; }} value={value} onChange={setValue} label="Message" id="session-prompt" />
    <div className="composer-toolbar"><span className="sr-only">Enter to send</span><Picker input={editor} edit={setValue} /></div>
  </>;
}
createRoot(document.getElementById("app")!).render(<Fixture />);

Object.assign(window, { clipboardFixture: {
  state,
  ready: () => !!input && !!document.querySelector("#session-prompt .monaco-editor"),
  focus: () => input!.focus(),
  selectAll: () => { input!.focus(); input!.setSelectionRange(0, input!.value.length); },
  caretAtEnd: () => { input!.focus(); input!.setSelectionRange(input!.value.length, input!.value.length); },
  pickerOpen: () => !!document.querySelector("dialog[open].reference-palette"),
  // The editor has the focus again: the element that takes its keys.
  editorFocused: () => !!document.activeElement?.closest("#session-prompt .monaco-editor"),
} });
