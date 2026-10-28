import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

// The window itself (main.tsx) over an isolated host: asking again for the changes of a project brings its Changes
// tab in front of the session shown in its pane, also when another pane has the keyboard.
test("Review changes brings the Changes tab in front of the session of its pane", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ evaluate, until, press, session, title, exceptions }) => {
    // The tabs of each pane, the one in front of it ending with "*".
    const panes = `[...document.querySelectorAll('.session-dock .flexlayout__tabset')].map(pane => [...pane.querySelectorAll('.flexlayout__tab_button')]
      .map(button => (button.textContent ?? '').trim() + (button.classList.contains('flexlayout__tab_button--selected') ? '*' : '')).join(' | '))`;
    const showing = (expected: string[], what: string) => until(`JSON.stringify(${panes})===${JSON.stringify(JSON.stringify(expected))}`, what);
    const entry = (text: string) => `[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].find(item => item.textContent.trim() === ${JSON.stringify(text)})`;
    const choose = async (text: string) => {
      await until(`!!${entry(text)}`, `the entry ${text}`);
      await press(entry(text));
      await until(`!document.querySelector('.session-tab-popup')`, `the menu closed after ${text}`);
    };
    const reviewChanges = async () => { await press(session("two"), 1, "right"); await choose("Review changes"); };
    // The notices of the fixture (cleanup status) can lie over the tabs of a narrow pane, whose tabs are below the title row.
    const tab = async (name: string) => {
      await evaluate("document.querySelectorAll('.bp6-toast button').forEach(button => button.click())");
      await until("!document.querySelector('.bp6-toast')", "the notices closed");
      await press(title(name));
    };

    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    // Two panes: session one, then session two beside it.
    await press(session("two"), 2);
    await press(title("two - Project"), 1, "right");
    await choose("Split right");
    await showing(["one - Project*", "two - Project*"], "two panes");
    // The changes open in a pane of their own; a session kept from there joins them and comes in front.
    await reviewChanges();
    await showing(["one - Project*", "two - Project*", "Changes Project*"], "the Changes tab in its pane");
    await press(`document.querySelector('.project-action-row > button[data-scope=""] .tree-twist')`);
    await until(`!!${session("chat-1")}`, "the chats, opened by their chevron");
    await press(session("chat-1"), 2);
    await showing(["one - Project*", "two - Project*", "Changes Project | chat-1 - Chat*"], "a session in front of the Changes tab");

    // Asked again, the changes come in front of that session: from the session itself, and from another pane.
    await reviewChanges();
    await showing(["one - Project*", "two - Project*", "Changes Project* | chat-1 - Chat"], "the Changes tab in front again");
    await tab("chat-1 - Chat");
    await showing(["one - Project*", "two - Project*", "Changes Project | chat-1 - Chat*"], "the session in front again");
    await tab("one - Project");
    await until(`${session("one")}.getAttribute('aria-pressed')==='true'`, "session one selected in the other pane");
    await showing(["one - Project*", "two - Project*", "Changes Project | chat-1 - Chat*"], "the session still in front of the Changes tab");
    await reviewChanges();
    await showing(["one - Project*", "two - Project*", "Changes Project* | chat-1 - Chat"], "the Changes tab in front, asked from another pane");
    // The panel of each tab is the one its button names: the one of the changes is shown, the one of the session is not.
    const shownPanel = (tab: string) => `(() => { const button = [...document.querySelectorAll('.session-dock .flexlayout__tab_button')].find(item => item.textContent.trim() === ${JSON.stringify(tab)});
      const panel = document.getElementById(button.getAttribute('aria-controls')); return getComputedStyle(panel).display !== 'none' && panel.getBoundingClientRect().width > 0; })()`;
    assert.equal(await evaluate(shownPanel("Changes Project")), true, "the panel of the changes is shown");
    assert.equal(await evaluate(shownPanel("chat-1 - Chat")), false, "the panel of the session behind it is not");
    assert.deepEqual(exceptions, []);
  });
});
