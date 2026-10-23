import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

// The window itself (main.tsx) over an isolated host: a press on a tab of the dock is the dock's, which prevents its
// mouse press. A popover open in the tab in front (a menu, the form that submits a review) still closes, as on any
// press outside it, and does not take the keyboard back from the tab shown, which would show its own tab again.
test("a press on another tab closes the popover of the tab in front and shows that tab", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true" }, async ({ evaluate, until, press, session, title, exceptions }) => {
    const front = `document.querySelector('.session-dock .flexlayout__tab_button--selected [data-session-node]')?.textContent.trim()`;
    await until(`!!${session("one")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await press(session("one"), 2);
    await evaluate(`document.querySelector('#project-list button[title="Open the code editor"]').click()`);
    await until(`!!document.querySelector('.project-editor .editor-new-file')`, "the code editor of the project");
    await press(`document.querySelector('.project-editor .editor-new-file')`);
    await until(`/^Editor/.test(${front})`, "the editor in front");

    await press(`document.querySelector('.project-editor button[aria-label="Editor options"]')`);
    await until(`!!document.querySelector('.editor-options')`, "the menu of the editor");
    await press(title("one - Project"));
    await until(`!document.querySelector('.editor-options') && ${front}==='one - Project'`, "the menu closed by the press on the tab of the session, that tab in front");
    // The keyboard goes to the prompt a few animation frames later: the session stays in front after them.
    await until(`!!document.activeElement?.closest('#session-prompt')`, "the prompt of the session has the keyboard");
    await evaluate(`new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(() => resolve(true), 200)))))`);
    assert.equal(await evaluate(front), "one - Project", "the session stays in front");
    assert.equal(await evaluate(`!!document.querySelector('.editor-options')`), false, "the menu stays closed");

    // The other way: a menu of the session, then a press on the tab of the editor.
    const sendOptions = `document.querySelector('.session-workspace .composer-send-options:not(:disabled)')`;
    await until(`!!${sendOptions}`, "the send options of the session");
    await press(sendOptions);
    await until(`!!document.querySelector('.bp6-popover [role="listbox"], .bp6-popover .bp6-menu')`, "the send options menu");
    const editorTitle = `[...document.querySelectorAll('.session-dock .flexlayout__tab_button [data-session-node]')].find(node => /^Editor/.test(node.textContent.trim()))`;
    await press(editorTitle);
    await until(`!document.querySelector('.bp6-popover .bp6-menu') && /^Editor/.test(${front})`, "the menu of the session closed by the press on the tab of the editor, that tab in front");
    await evaluate(`new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(() => resolve(true), 200)))))`);
    assert.match(await evaluate(front) as string, /^Editor/, "the editor stays in front");
    assert.deepEqual(exceptions, []);
  });
});
