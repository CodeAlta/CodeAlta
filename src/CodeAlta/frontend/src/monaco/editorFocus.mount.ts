import { monaco } from "./monacoEnvironment";

// Two editors of the page, as a prompt and a file are. Literal fixtures only; no application host.
// "#textarea" takes the input of an engine without EditContext, the one of macOS.
const editContext = location.hash !== "#textarea";
const create = (id: string) => {
  const host = document.createElement("div");
  host.id = id;
  host.style.cssText = "width: 480px; height: 120px; margin: 8px;";
  document.getElementById("app")!.appendChild(host);
  return monaco.editor.create(host, { value: "", ariaLabel: id, automaticLayout: false, minimap: { enabled: false }, editContext });
};
const first = create("first"), second = create("second");
const editors = { first, second };
const focusEvents = ["focus", "blur", "focusin", "focusout"];

Object.assign(window, { editorFocusFixture: {
  focus(name: keyof typeof editors) { editors[name].focus(); },
  // Gives an editor the focus as a page does in a window that is in the background: the browser tells nobody,
  // neither the editor that takes it nor the one that had it. What the browser sends here is kept from the page;
  // what the page sends itself is not.
  focusInBackground(name: keyof typeof editors) {
    const unheard = (event: Event) => { if (event.isTrusted) event.stopImmediatePropagation(); };
    for (const type of focusEvents) window.addEventListener(type, unheard, true);
    try { editors[name].focus(); } finally { for (const type of focusEvents) window.removeEventListener(type, unheard, true); }
  },
  state() { return [first, second].map(editor => ({ focus: editor.hasTextFocus(), value: editor.getValue() })); },
  input() { return document.activeElement?.tagName.toLowerCase() ?? ""; },
} });
