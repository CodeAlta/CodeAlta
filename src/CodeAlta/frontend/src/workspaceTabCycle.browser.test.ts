import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";
import { testLimit } from "./testTime";

type Command = (method: string, params?: object) => Promise<unknown>;
type Until = (expression: string, what: string) => Promise<void>;
// Ctrl held while Tab is pressed `presses` times: the tab the switcher is on when Ctrl is released is shown.
const holdTabs = async (command: Command, until: Until, presses: number, shift = false) => {
  const flags = 2 | (shift ? 8 : 0);
  await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Control", code: "ControlLeft", windowsVirtualKeyCode: 17, modifiers: 2 });
  for (let press = 0; press < presses; press++) {
    await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: flags });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: flags });
  }
  await until(`!!document.querySelector('.tab-switcher')`, "the list of the tabs while Ctrl is held");
  await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Control", code: "ControlLeft", windowsVirtualKeyCode: 17, modifiers: 0 });
  await until(`!document.querySelector('.tab-switcher')`, "the list closed when Ctrl is released");
};

// The window itself (main.tsx) over an isolated host: Ctrl+Tab and Ctrl+Shift+Tab go through tabs of the current pane by how
// recently each was shown, the tools and the sessions alike, and Ctrl+PageDown, Ctrl+PageUp and
// Ctrl+Alt+Left and Right go through them in the order of the strip; none opens a tab. A code editor that has the keyboard
// goes through its own files with Ctrl+Tab.
test("Ctrl+Tab and Ctrl+Shift+Tab go through the tabs of the window by how recently they were shown", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, tabs, shown, press, session, title, exceptions }) => {
    // A real key press, with Ctrl (2), Alt (1) and Shift (8) as asked.
    const key = async (name: "Tab" | "PageDown" | "PageUp" | "ArrowLeft" | "ArrowRight", modifiers: { shift?: boolean; alt?: boolean } = {}) => {
      const code = { Tab: 9, PageDown: 34, PageUp: 33, ArrowLeft: 37, ArrowRight: 39 }[name];
      const flags = 2 | (modifiers.alt ? 1 : 0) | (modifiers.shift ? 8 : 0);
      await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: name, code: name, windowsVirtualKeyCode: code, modifiers: flags });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key: name, code: name, windowsVirtualKeyCode: code, modifiers: flags });
    };
    const cycle = (presses: number, shift = false) => holdTabs(command, until, presses, shift);
    const strip = (front: string) => ["one - Project", "Automations", "Work items", "Issues", "Workflows"].map(name => name === front ? `${name}*` : name);
    const inPrompt = `!!document.activeElement?.closest('#session-prompt')`;

    await until(`!!${session("one")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await press(session("one"), 2);
    // Four tools whose buttons have room in the bar beside the Explorer.
    for (const tool of ["automations", "work", "issues", "workflows"]) await press(`document.querySelector('.activity-rail .activity-${tool}')`);
    await shown(strip("Workflows"), "a session and four tools, the last one in front");

    // From the prompt of the session: the tab shown before, then back; held, the tabs shown longer ago. The first press goes
    // to the tab shown last, and Shift starts from the one shown longest ago.
    await press(title("one - Project"));
    await shown(strip("one - Project"), "the session in front");
    await until(inPrompt, "the prompt has the keyboard");
    await cycle(1);
    await shown(strip("Workflows"), "Ctrl+Tab from the prompt shows the tab shown before");
    await cycle(1);
    await shown(strip("one - Project"), "Ctrl+Tab again goes back to the session, not on in the strip, and opens no new session");
    await until(inPrompt, "the prompt has the keyboard again");
    await cycle(2);
    await shown(strip("Issues"), "Ctrl+Tab twice with Ctrl held shows the tab shown before the last one");
    await cycle(1, true);
    await shown(strip("Automations"), "Ctrl+Shift+Tab goes around to the tab shown longest ago");

    // From the header of a tab: the same.
    for (const [shift, expected] of [[false, "Automations"], [true, "Work items"]] as const) {
      await press(title("Workflows"));
      await shown(strip("Workflows"), "a click on the header of Workflows");
      await evaluate(`${title("Workflows")}.closest('[role="tab"]').focus()`);
      await cycle(1, shift);
      await shown(strip(expected), `Ctrl+${shift ? "Shift+" : ""}Tab from the header of Workflows`);
    }
    await press(title("one - Project"));
    await shown(strip("one - Project"), "the session in front");

    // The other shortcuts of the same commands go the same way and open no tab either.
    await key("ArrowLeft", { alt: true });
    await shown(strip("Workflows"), "Ctrl+Alt+Left from the session shows the tab before it, around the strip");
    await key("ArrowRight", { alt: true });
    await shown(strip("one - Project"), "Ctrl+Alt+Right");
    await key("PageDown");
    await shown(strip("Automations"), "Ctrl+PageDown");
    await key("PageUp");
    await shown(strip("one - Project"), "Ctrl+PageUp");

    // A code editor that has the keyboard keeps Ctrl+Tab for its files, the file shown before first; Ctrl+PageDown still leaves it.
    await evaluate(`document.querySelector('#project-list button[title="Open the code editor"]').click()`);
    await until(`!!document.querySelector('.project-editor .editor-new-file')`, "the code editor of the project");
    for (let file = 0; file < 3; file++) await press(`document.querySelector('.project-editor .editor-new-file')`);
    const activeFile = `document.querySelector('.project-editor .editor-tab[aria-selected="true"] .editor-tab-name')?.textContent`;
    await until(`${activeFile}==='Untitled-3'`, "three new files in the editor");
    const front = `document.querySelector('.session-dock .flexlayout__tab_button--selected [data-session-node]')?.textContent.trim()`;
    const editor = await evaluate(front) as string;
    assert.match(editor, /^Editor/);
    await until(`!!document.querySelector('.project-editor .monaco-editor :is(.inputarea, .native-edit-context)')`, "the text of the editor");
    await evaluate(`document.querySelector('.project-editor .monaco-editor :is(.inputarea, .native-edit-context)').focus()`);
    // Command+Tab belongs to macOS, not a file switcher that never sees a Control release.
    await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: 4 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: 4 });
    await evaluate(`new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))`);
    assert.equal(await evaluate(`!!document.querySelector('.tab-switcher')`), false);
    await cycle(1);
    await until(`${activeFile}==='Untitled-2'`, "Ctrl+Tab in the editor shows the file shown before");
    assert.equal(await evaluate(front), editor, "the editor stays in front");
    await cycle(1);
    await until(`${activeFile}==='Untitled-3'`, "Ctrl+Tab in the editor goes back to the file left");
    await cycle(2);
    await until(`${activeFile}==='Untitled-1'`, "Ctrl+Tab held in the editor goes further back");
    assert.equal(await evaluate(front), editor, "the editor stays in front");
    const count = (await evaluate(tabs) as string[]).length;
    await key("PageDown");
    await until(`${front}==='one - Project'`, "Ctrl+PageDown from the editor goes around to the first tab of the window");
    assert.equal((await evaluate(tabs) as string[]).length, count, "no tab was opened or closed");
    assert.deepEqual(exceptions, []);
  });
});

// The transient tab has its title in italics in the strip, and so in the list of Ctrl+Tab.
test("the list of Ctrl+Tab shows the tab that is only looked at in italics", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, press, session, exceptions }) => {
    await until(`!!${session("two")}`, "the sessions in the list");
    await press(session("two"));
    await until(`!!document.querySelector('.session-dock .flexlayout__tab_button--selected [data-transient="true"]')`, "session two looked at, in the transient tab");
    await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Control", code: "ControlLeft", windowsVirtualKeyCode: 17, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: 2 });
    await until(`!!document.querySelector('.tab-switcher')`, "the list of the tabs while Ctrl is held");
    const style = (label: string) => evaluate(`getComputedStyle([...document.querySelectorAll('.tab-switcher-label')].find(node => node.textContent === ${JSON.stringify(label)})).fontStyle`);
    assert.deepEqual([await style("two - Project"), await style("one - Project")], ["italic", "normal"]);
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Control", code: "ControlLeft", windowsVirtualKeyCode: 17, modifiers: 0 });
    assert.deepEqual(exceptions, []);
  });
});

// As in VS Code, the list is that of a group: the tabs of the pane in front, not those of the other panes.
test("Ctrl+Tab goes through the tabs of the pane in front only", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, press, session, title, exceptions }) => {
    const panes = `[...document.querySelectorAll('.session-dock .flexlayout__tabset')].map(pane => [...pane.querySelectorAll('.flexlayout__tab_button')]
      .map(button => (button.textContent ?? '').trim() + (button.classList.contains('flexlayout__tab_button--selected') ? '*' : '')).join(' | '))`;
    const showing = (expected: string[], what: string) => until(`JSON.stringify(${panes})===${JSON.stringify(JSON.stringify(expected))}`, what);
    const entry = (text: string) => `[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].find(item => item.textContent.trim() === ${JSON.stringify(text)})`;
    const tab = async (name: string) => {
      await evaluate("document.querySelectorAll('.bp6-toast button').forEach(button => button.click())");
      await until("!document.querySelector('.bp6-toast')", "the notices closed");
      await press(title(name));
    };
    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    // The changes open in a pane of their own, and a chat kept from there joins them.
    await press(session("one"), 1, "right");
    await until(`!!${entry("Review changes")}`, "the entry Review changes");
    await press(entry("Review changes"));
    await showing(["one - Project*", "Changes Project*"], "the Changes tab in its own pane");
    await press(`document.querySelector('.project-action-row > button[data-scope=""] .tree-twist')`);
    await until(`!!${session("chat-1")}`, "the chats, opened by their chevron");
    await press(session("chat-1"), 2);
    await showing(["one - Project*", "Changes Project | chat-1 - Chat*"], "a chat in the pane of the changes");
    // The pane of session one was shown after the changes: the list of the pane in front still holds its own tabs only.
    await tab("one - Project");
    await tab("chat-1 - Chat");
    await showing(["one - Project*", "Changes Project | chat-1 - Chat*"], "the chat in front again");
    await holdTabs(command, until, 1);
    await showing(["one - Project*", "Changes Project* | chat-1 - Chat"], "Ctrl+Tab goes to the Changes tab of its pane, not to session one of the other");
    await holdTabs(command, until, 1);
    await showing(["one - Project*", "Changes Project | chat-1 - Chat*"], "and back to the chat");
    assert.deepEqual(exceptions, []);
  });
});
