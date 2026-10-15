import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

// The window itself (main.tsx) over an isolated host: the menu of a tab, opened with the other button of the pointer,
// and the menu of a pane close the tabs of that pane in one go, and keep the transient tab.
test("the menu of a tab and the menu of its pane close the other tabs, the ones after it or all of them", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, shown, press, session, scope, title, exceptions }) => {
    const entries = `[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].map(item => item.textContent.trim() + (item.getAttribute('aria-disabled') === 'true' ? ' (off)' : ''))`;
    const entry = (text: string) => `[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].find(item => item.textContent.trim() === ${JSON.stringify(text)})`;
    // Opens the menu of a tab, or of a pane by its button, and checks what it offers.
    const menu = async (selector: string, button: "left" | "right", expected: string[], what: string) => {
      await press(selector, 1, button);
      await until(`document.querySelector('.session-tab-popup')`, `the menu of ${what}`);
      assert.deepEqual(await evaluate(entries), expected, what);
    };
    const choose = async (text: string) => {
      await press(entry(text));
      await until(`!document.querySelector('.session-tab-popup')`, `the menu closed after ${text}`);
    };
    const keep = async (ids: string[]) => { for (const id of ids) await press(session(id), 2); };
    const more = `document.querySelector('.session-dock .session-tab-more')`;

    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await press(`document.querySelector('.project-action-row > button[data-scope=""] .tree-twist')`);
    await until(`!!${session("chat-1")}`, "the chats, opened by their chevron");
    await keep(["two", "chat-1", "chat-2"]);
    await shown(["one - Project", "two - Project", "chat-1 - Chat", "chat-2 - Chat*"], "four kept tabs");

    // The menu of a tab that is not the one in front: asking for it does not show the tab.
    await menu(title("two - Project"), "right", ["Close", "Close others", "Close to the right", "Close all", "Split right", "Split below"], "a kept tab");
    await shown(["one - Project", "two - Project", "chat-1 - Chat", "chat-2 - Chat*"], "the same tab in front");
    // The tabs after it close, the one in front among them: the tab the menu was asked from is shown.
    await choose("Close to the right");
    await shown(["one - Project", "two - Project*"], "the tabs after it closed");
    await until(`${session("two")}.getAttribute('aria-pressed')==='true'`, "its session selected");
    // The last tab of a pane has nothing after it.
    await menu(title("two - Project"), "right", ["Close", "Close others", "Close to the right (off)", "Close all", "Split right", "Split below"], "the last tab");
    // A press on the tab in front, beside the menu, closes the menu and changes nothing else. As any click on a session
    // tab or change of the tabs, it hands the keyboard to the prompt two animation frames later, maybe more than once:
    // the frames already asked for are let run, or the prompt takes the keyboard from the tab the keys are pressed on below.
    const edge = await evaluate(`(() => { const r = ${title("two - Project")}.closest('.flexlayout__tab_button').getBoundingClientRect(); return { x: r.left + 5, y: r.top + r.height / 2 }; })()`);
    for (const type of ["mouseMoved", "mousePressed", "mouseReleased"]) await command("Input.dispatchMouseEvent", { type, ...edge, button: type === "mouseMoved" ? "none" : "left", clickCount: 1 });
    await until(`!document.querySelector('.session-tab-popup') && document.activeElement?.closest('#session-prompt')`, "the menu closed by a press on its tab, the keyboard in the prompt");
    await evaluate(`new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true)))))`);
    await shown(["one - Project", "two - Project*"], "the same tabs");
    // The menu key and Shift+F10 ask for the menu of the tab that has the keyboard, on every system (macOS turns neither
    // into a request for a context menu: the tabs read the keys); Escape closes it and gives the keyboard back.
    const focused = `document.activeElement === ${title("one - Project")}.closest('[role="tab"]')`;
    await evaluate(`${title("one - Project")}.closest('[role="tab"]').focus()`);
    assert.equal(await evaluate(focused), true, "a tab takes the keyboard");
    for (const [what, key] of [["the menu key", { key: "ContextMenu", code: "ContextMenu", windowsVirtualKeyCode: 93 }],
      ["Shift+F10", { key: "F10", code: "F10", windowsVirtualKeyCode: 121, modifiers: 8 }]] as const) {
      for (const type of ["rawKeyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, ...key });
      // The menu takes the keyboard once it is shown, on its first entry: only from there does Escape give it back.
      await until(`document.querySelector('.session-tab-popup [role="menu"]')?.getAttribute('aria-label') === 'one - Project' && document.activeElement?.closest('.session-tab-popup')`,
        `the menu of the tab that has the keyboard, asked with ${what}`);
      assert.deepEqual(await evaluate(entries), ["Close", "Close others", "Close to the right", "Close all", "Split right", "Split below"], what);
      for (const type of ["rawKeyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
      await until(`!document.querySelector('.session-tab-popup') && ${focused}`, `the menu of ${what} closed by Escape, the keyboard back on its tab`);
      await shown(["one - Project", "two - Project*"], "the same tabs");
    }

    // The transient tab is kept from its menu: the prompt of a new session, and a session that is looked at.
    await keep(["chat-1"]);
    await press(scope("other"));
    await shown(["one - Project", "two - Project", "chat-1 - Chat", "~New session — Other project*"], "the prompt of a project, in the transient tab");
    await menu(title("New session — Other project"), "right", ["Close", "Close others", "Close to the right (off)", "Close all", "Keep open"], "the transient prompt");
    await choose("Keep open");
    await shown(["one - Project", "two - Project", "chat-1 - Chat", "New session — Other project*"], "the prompt, kept");
    await press(session("chat-2"));
    await shown(["one - Project", "two - Project", "chat-1 - Chat", "New session — Other project", "~chat-2 - Chat*"], "a session looked at");
    await menu(title("chat-2 - Chat"), "right", ["Close", "Close others", "Close to the right (off)", "Close all", "Keep open", "Split right", "Split below"], "the transient session");
    await choose("Keep open");
    await shown(["one - Project", "two - Project", "chat-1 - Chat", "New session — Other project", "chat-2 - Chat*"], "the session, kept");

    // The others of a tab close: sessions and the prompt of a new session alike. The tab is shown in their place.
    await menu(title("one - Project"), "right", ["Close", "Close others", "Close to the right", "Close all", "Split right", "Split below"], "the first tab");
    await choose("Close others");
    await shown(["one - Project*"], "the only tab left");
    await until(`${session("one")}.getAttribute('aria-pressed')==='true'`, "its session selected");
    await menu(title("one - Project"), "right", ["Close", "Close others (off)", "Close to the right (off)", "Close all", "Split right (off)", "Split below (off)"], "a tab alone in its pane");
    // Close closes the tab the menu was asked from; the prompt of a new session is what is left.
    await choose("Close");
    await shown(["~New session — Project*"], "no tab left but the transient prompt");
    await menu(title("New session — Project"), "right", ["Close (off)", "Close others (off)", "Close to the right (off)", "Close all (off)", "Keep open"], "the transient prompt alone");
    await press(`document.querySelector('.session-dock .flexlayout__tab')`);
    await until(`!document.querySelector('.session-tab-popup')`, "the menu closed by a click outside");

    // The menu of the pane closes about the tab in front of it, and reopens what was closed.
    await keep(["one", "two", "chat-1"]);
    await press(title("two - Project"));
    await shown(["one - Project", "two - Project*", "chat-1 - Chat"], "three kept tabs, the second in front");
    await menu(more, "left", ["Split right", "Split below", "Close others", "Close all", "Reopen closed tab", "Refresh statuses"], "the pane");
    await choose("Close others");
    await shown(["two - Project*"], "the others of the tab in front closed");
    await keep(["one"]);
    await shown(["two - Project", "one - Project*"], "two tabs again");
    await press(more);
    await until(`document.querySelector('.session-tab-popup')`, "the menu of the pane");
    await choose("Close all");
    await shown(["~New session — Project*"], "every tab of the pane closed");
    await press(more);
    await until(`document.querySelector('.session-tab-popup')`, "the menu of the pane");
    await choose("Reopen closed tab");
    await shown(["one - Project*"], "the tab that was closed last, back");
    assert.deepEqual(exceptions, []);
  });
});
