import { useState } from "react";
import { createRoot } from "react-dom/client";
import { EditMenu } from "./EditMenu";
import { fixtureClipboard, middleOf, readEditMenu } from "./editMenu.probe";

// Text fields of every kind, a control with a menu of its own, and text of the page, under the menu of a right-click.
// The clipboard is the fixture's own; the copy of the page's selection is caught before it reaches the system's.
const state = { value: "hello world", clipboard: "pasted ", copied: "", blurs: 0, own: 0, escapes: 0 };
const clipboard = fixtureClipboard(state);

function Fixture() {
  const [value, setValue] = useState(state.value);
  return <div style={{ padding: 40, display: "grid", gap: 12, width: 360 }}>
    <input id="editable" value={value} onChange={event => { state.value = event.target.value; setValue(state.value); }} onBlur={() => state.blurs++} />
    <input id="readonly" readOnly value="read only text" />
    <input id="password" type="password" defaultValue="secret" />
    <input id="disabled" disabled defaultValue="disabled" />
    <fieldset disabled style={{ margin: 0, padding: 0, border: 0 }}><input id="fieldset" defaultValue="in a disabled fieldset" /></fieldset>
    <input id="aria" aria-readonly="true" defaultValue="read only for assistive technologies" />
    <textarea id="area" defaultValue={"line one\nline two"} />
    <div onContextMenu={event => { event.preventDefault(); state.own++; }}><input id="owned" defaultValue="a menu of its own" /></div>
    <p id="text">Some text of the page.</p>
    {/* Settings and the other windows of the application are modal dialogs: the top layer of the page. */}
    <dialog id="modal"><input id="inDialog" defaultValue="in a modal dialog" /></dialog>
    <EditMenu clipboard={clipboard} />
  </div>;
}

document.addEventListener("keydown", event => { if (event.key === "Escape") state.escapes++; });
document.addEventListener("copy", event => {
  state.copied = getSelection()?.toString() ?? "";
  event.clipboardData?.setData("text/plain", state.copied);
  event.preventDefault();
});
createRoot(document.getElementById("root")!).render(<Fixture />);

const field = (id: string) => document.getElementById(id) as HTMLInputElement;
Object.assign(window, { textFieldFixture: {
  state,
  ready: () => !!document.getElementById("text"),
  /** Focuses a field and selects a part of it. */
  select(id: string, start: number, end: number) { field(id).focus(); field(id).setSelectionRange(start, end); },
  /** Asks for the menu of an element as a right-click does, at its middle. */
  open(id: string) {
    const { x, y } = middleOf(field(id));
    return field(id).dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, button: 2, clientX: x, clientY: y }));
  },
  /** The middle of an element, in the coordinates of the page. */
  point: (id: string) => middleOf(field(id)),
  menu: readEditMenu,
  focused: () => document.activeElement?.id ?? null,
  selection: (id: string) => field(id).value.slice(field(id).selectionStart ?? 0, field(id).selectionEnd ?? 0),
  selectText() { getSelection()!.selectAllChildren(document.getElementById("text")!); },
  showModal() { (document.getElementById("modal") as HTMLDialogElement).showModal(); },
  /** Whether the first entry of the open menu is what the page shows at its place: not under a dialog. */
  menuOnTop() {
    const item = document.querySelector(".edit-menu [role=menuitem]");
    const box = item?.getBoundingClientRect();
    return !!box && !!item!.contains(document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2));
  },
} });
