import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

// The window itself (main.tsx) over an isolated host, driven with the pointer: with the setting of Settings > Sessions
// the sessions share a single tab. Turned on, it leaves the session shown and closes the other tabs; from there whatever
// is opened takes that tab, nothing in it is transient, and the prompt of a new session keeps its text without a tab.
// Turned off, the tabs are what they were before it.
test("with a single tab for sessions, what is opened takes the place of the session shown", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, tabs, shown, press, session, scope, title, exceptions }) => {
    const setting = `document.querySelector('#settings-single-session-tab')`;
    const kept = `localStorage.getItem('codealta.desktop.single-session-tab.v1')`;
    const storedTabs = `JSON.parse(localStorage.getItem('codealta.desktop.sessionTabs.v1') ?? 'null')`;
    // Turns the setting on or off on the page of the sessions, and closes Settings.
    const set = async (on: boolean) => {
      await press(`document.querySelector('button.activity-settings')`);
      await until(`!!document.querySelector('[data-settings-section="sessions"]')`, "Settings");
      await press(`document.querySelector('[data-settings-section="sessions"]')`);
      await until(`!!${setting}`, "the setting of the tabs, on the page of the sessions");
      assert.equal(await evaluate(`${setting}.checked`), !on);
      await press(`${setting}.parentElement.querySelector('.bp6-control-indicator')`);
      await until(`${setting}.checked === ${on} && ${kept} === ${JSON.stringify(on ? "on" : "off")}`, `the setting turned ${on ? "on" : "off"} and kept`);
      await press(`document.querySelector('button[aria-label="Close settings"]')`);
      await until(`!document.querySelector('.settings-dialog')`, "Settings closed");
    };
    const entries = `[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].map(item => item.textContent.trim() + (item.getAttribute('aria-disabled') === 'true' ? ' (off)' : ''))`;
    const prompt = `[...document.querySelectorAll('#catalog-prompt')].find(node => node.getBoundingClientRect().width > 0)`;
    const style = (text: string) => evaluate(`getComputedStyle(${title(text)}).fontStyle`);

    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    assert.equal(await evaluate(kept), null, "nothing is kept of a setting nobody touched");
    // Several tabs, as without the setting: a kept session, a kept chat that is the one shown, and a session looked at.
    await press(`document.querySelector('.project-action-row > button[data-scope=""] .tree-twist')`);
    await until(`!!${session("chat-1")}`, "the chats, opened by their chevron");
    await press(session("chat-1"), 2);
    await shown(["one - Project", "chat-1 - Chat*"], "a chat, kept");
    await press(session("two"));
    await shown(["one - Project", "chat-1 - Chat", "~two - Project*"], "a session looked at beside the kept ones");
    await press(title("chat-1 - Chat"));
    await shown(["one - Project", "chat-1 - Chat*", "~two - Project"], "the kept chat in front");

    // Turned on: the session shown stays, the other tabs close.
    await set(true);
    await shown(["chat-1 - Chat*"], "the session shown, alone");
    await until(`${storedTabs}?.open.length === 1`, "the single tab kept for the next start");
    assert.deepEqual(Object.keys(await evaluate(storedTabs)).sort(), ["active", "open", "version"], "what is kept is what a window with one tab keeps");

    // A session that is looked at takes the tab; so does one opened with a double click. Neither is transient.
    await press(session("two"));
    await shown(["two - Project*"], "the session looked at, in the place of the one that was shown");
    assert.equal(await style("two - Project"), "normal");
    await press(session("one"), 2);
    await shown(["one - Project*"], "the session of the row that was double-clicked");
    await press(title("one - Project"), 2);
    await shown(["one - Project*"], "a double click on the title changes nothing");
    assert.equal(await evaluate(`${session("one")}.getAttribute('aria-pressed')`), "true");

    // A prompt that waits above the composer of a session keeps the pane of that session when another takes the tab:
    // the pane is what sends it once the session is idle. It is out of sight, no tab, and leaves with the prompt.
    const pane = `[...document.querySelectorAll('.session-workspace')].find(node => node.getBoundingClientRect().width > 0)`;
    const waiting = `[...document.querySelectorAll('.session-background-panes .session-workspace')].map(node => node.querySelectorAll('.composer-queue-row').length)`;
    await press(`${pane}.querySelector('.composer-send-options')`);
    await until(`[...document.querySelectorAll('.bp6-menu-item')].some(item => item.textContent.includes('Enqueue until idle'))`, "the options of Send");
    await press(`[...document.querySelectorAll('.bp6-menu-item')].find(item => item.textContent.includes('Enqueue until idle'))`);
    await until(`!!${pane}.querySelector('.composer-send-group > button[aria-label="Enqueue"]')`, "Send enqueues");
    await evaluate(`${pane}.querySelector('#session-prompt').focus()`);
    await command("Input.insertText", { text: "Later" });
    await press(`${pane}.querySelector('.composer-send-group > button[aria-label="Enqueue"]')`);
    await until(`${pane}.querySelectorAll('.composer-queue-row').length === 1`, "a prompt waiting above the composer");
    assert.equal(await evaluate(`!!document.querySelector('.session-background-panes')`), false, "the session shown has its pane in its tab");
    await press(session("two"));
    await shown(["two - Project*"], "another session in the tab");
    await until(`JSON.stringify(${waiting}) === '[1]'`, "the pane of the session whose prompt waits, kept");
    assert.equal(await evaluate(`document.querySelector('.session-background-panes').getBoundingClientRect().width`), 0, "out of sight");
    await press(session("one"));
    await shown(["one - Project*"], "the session with the waiting prompt, in the tab again");
    await until(`${pane}.querySelectorAll('.composer-queue-row').length === 1 && !document.querySelector('.session-background-panes')`, "its prompt still waits, in the pane of the tab");
    await press(`${pane}.querySelector('.composer-queue-delete')`);
    await until(`${pane}.querySelectorAll('.composer-queue-row').length === 0`, "the prompt taken off the queue");
    await press(session("two"));
    await shown(["two - Project*"], "another session in the tab");
    await press(session("one"));
    await shown(["one - Project*"], "back");
    assert.equal(await evaluate(`!!document.querySelector('.session-background-panes')`), false, "no pane is kept for a session with nothing waiting");

    // The menu of the tab closes it; there is nothing to keep open.
    await press(title("one - Project"), 1, "right");
    await until(`document.querySelector('.session-tab-popup')`, "the menu of the tab");
    assert.deepEqual(await evaluate(entries), ["Close", "Close others (off)", "Close to the right (off)", "Close all", "Split right (off)", "Split below (off)"]);
    for (const type of ["rawKeyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await until(`!document.querySelector('.session-tab-popup')`, "the menu closed by Escape");

    // The row of a project shows the prompt of a new session of it in the tab, in the place of the session.
    await press(scope("other"));
    await shown(["New session — Other project*"], "the prompt of the project, in the tab");
    assert.equal(await style("New session — Other project"), "normal");
    // Alone in its pane, it has no menu, and a double click does not give it a tab of its own.
    await press(title("New session — Other project"), 1, "right");
    await press(title("New session — Other project"), 2);
    await shown(["New session — Other project*"], "the prompt, as it was");
    assert.equal(await evaluate(`!!document.querySelector('.session-tab-popup')`), false, "no menu with nothing to offer");

    // What is typed in it stays with its project when a session takes the tab, and is there when the prompt is shown again.
    await until(`!!${prompt}`, "the prompt of a new session");
    await evaluate(`${prompt}.focus()`);
    await command("Input.insertText", { text: "A draft for the other project" });
    await until(`(localStorage.getItem('codealta.desktop.localPrompt."other"') ?? '').includes('A draft for the other project')`, "the text is kept with its project");
    await press(session("two"));
    await shown(["two - Project*"], "a session in the tab, and no tab for the prompt that was left");
    await press(scope("other"));
    await shown(["New session — Other project*"], "the prompt of the project again");
    await until(`(${prompt}?.querySelector('.view-lines')?.textContent ?? '').replaceAll('\\u00a0', ' ').includes('A draft for the other project')`, "the text that was typed");

    // Closing the tab of a session leaves the prompt of a new session, and the tab that was closed opens again.
    await press(session("two"));
    await shown(["two - Project*"], "a session in the tab");
    await press(`${title("two - Project")}.closest('.flexlayout__tab_button').querySelector('.flexlayout__tab_button_trailing')`);
    await shown(["New session — Project*"], "the prompt of a new session, once the tab is closed");
    await press(`document.querySelector('.session-dock .session-tab-more')`);
    await until(`document.querySelector('.session-tab-popup')`, "the menu of the pane");
    await press(`[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].find(item => item.textContent.trim() === 'Reopen closed tab')`);
    await shown(["two - Project*"], "the tab that was closed, back");

    // The next start has the setting and the tab.
    await command("Page.reload");
    await until(`!!${session("two")} && ${tabs}.length === 1`, "the window loaded again");
    await shown(["two - Project*"], "the single tab, as it was");
    await press(session("one"));
    await shown(["one - Project*"], "a session still takes the tab");

    // Turned off: the tab is a kept one, and the next session looked at gets the transient tab beside it.
    await set(false);
    await shown(["one - Project*"], "the same tab");
    await press(session("two"));
    await shown(["one - Project", "~two - Project*"], "a session looked at, beside the tab that stays");
    assert.equal(await style("two - Project"), "italic");
    assert.deepEqual(exceptions, []);
  });
});
