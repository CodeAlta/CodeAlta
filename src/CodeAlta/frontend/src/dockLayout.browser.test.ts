import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { dockLayoutKey } from "./dockLayout";
import { withSessionWindow } from "./sessionWindowHarness";
import { testLimit } from "./testTime";

// The window itself (main.tsx) over an isolated host: the panes of the dock are what they were at the next start, as the
// editor groups of VS Code are, with the tabs in each and the one in front of each.
test("the panes of the dock come back at the next start", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, press, session, title, exceptions }) => {
    const panes = `[...document.querySelectorAll('.session-dock .flexlayout__tabset')].map(pane => [...pane.querySelectorAll('.flexlayout__tab_button')]
      .map(button => (button.textContent ?? '').trim() + (button.classList.contains('flexlayout__tab_button--selected') ? '*' : '')).join(' | '))`;
    const showing = (expected: string[], what: string) => until(`JSON.stringify(${panes})===${JSON.stringify(JSON.stringify(expected))}`, what);
    const entry = (text: string) => `[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].find(item => item.textContent.trim() === ${JSON.stringify(text)})`;
    const kept = `localStorage.getItem(${JSON.stringify(dockLayoutKey)})`;

    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    // Two panes: session one, then session two beside it.
    await press(session("two"), 2);
    await press(title("two - Project"), 1, "right");
    await until(`!!${entry("Split right")}`, "the entry Split right");
    await press(entry("Split right"));
    await showing(["one - Project*", "two - Project*"], "two panes");
    // The arrangement is kept shortly after the change.
    await until(`(() => { try { return JSON.parse(${kept}).layout.children.length === 2; } catch { return false; } })()`, "the two panes kept");

    await command("Page.reload");
    await until(`!!${session("two")}`, "the window loaded again");
    await showing(["one - Project*", "two - Project*"], "the two panes at the next start, each with its tab in front");
    assert.deepEqual(exceptions, []);
  });
});
