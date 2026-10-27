import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

// The window itself (main.tsx) over an isolated host: Ctrl+Tab and Ctrl+Shift+Tab go through every tab of the dock as
// the strip shows them, the tools and the sessions alike, around the strip, wherever the keyboard is; they open no tab.
// A code editor that has the keyboard goes through its own files with them.
test("Ctrl+Tab and Ctrl+Shift+Tab go through the tabs of the window in the order of the strip", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, tabs, shown, press, session, title, exceptions }) => {
    // A real key press, with Ctrl (2), Alt (1) and Shift (8) as asked.
    const key = async (name: "Tab" | "PageDown" | "PageUp" | "ArrowLeft" | "ArrowRight", modifiers: { shift?: boolean; alt?: boolean } = {}) => {
      const code = { Tab: 9, PageDown: 34, PageUp: 33, ArrowLeft: 37, ArrowRight: 39 }[name];
      const flags = 2 | (modifiers.alt ? 1 : 0) | (modifiers.shift ? 8 : 0);
      await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: name, code: name, windowsVirtualKeyCode: code, modifiers: flags });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key: name, code: name, windowsVirtualKeyCode: code, modifiers: flags });
    };
    const strip = (front: string) => ["one - Project", "Automations", "Work items", "Issues", "Workflows"].map(name => name === front ? `${name}*` : name);
    const inPrompt = `!!document.activeElement?.closest('#session-prompt')`;

    await until(`!!${session("one")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await press(session("one"), 2);
    // Four tools whose buttons have room in the bar beside the Explorer.
    for (const tool of ["automations", "work", "issues", "workflows"]) await press(`document.querySelector('.activity-rail .activity-${tool}')`);
    await shown(strip("Workflows"), "a session and four tools, the last one in front");

    // From the prompt of the session: forward and backward, around the strip both ways.
    await press(title("one - Project"));
    await shown(strip("one - Project"), "the session in front");
    await until(inPrompt, "the prompt has the keyboard");
    await key("Tab");
    await shown(strip("Automations"), "Ctrl+Tab from the prompt shows the next tab");
    await key("Tab", { shift: true });
    await shown(strip("one - Project"), "Ctrl+Shift+Tab shows the tab before");
    await until(inPrompt, "the prompt has the keyboard again");
    await key("Tab", { shift: true });
    await shown(strip("Workflows"), "Ctrl+Shift+Tab from the first tab goes around to the last one, without opening a new session");
    await key("Tab");
    await shown(strip("one - Project"), "Ctrl+Tab from the last tab goes around to the first one");

    // From the header of a tab: the same direction.
    for (const [shift, expected] of [[true, "Issues"], [false, "one - Project"]] as const) {
      await press(title("Workflows"));
      await shown(strip("Workflows"), "a click on the header of Workflows");
      await evaluate(`${title("Workflows")}.closest('[role="tab"]').focus()`);
      await key("Tab", { shift });
      await shown(strip(expected), `Ctrl+${shift ? "Shift+" : ""}Tab from the header of Workflows`);
    }

    // The other shortcuts of the same commands go the same way and open no tab either.
    await key("ArrowLeft", { alt: true });
    await shown(strip("Workflows"), "Ctrl+Alt+Left from the session shows the tab before it, around the strip");
    await key("ArrowRight", { alt: true });
    await shown(strip("one - Project"), "Ctrl+Alt+Right");
    await key("PageDown");
    await shown(strip("Automations"), "Ctrl+PageDown");
    await key("PageUp");
    await shown(strip("one - Project"), "Ctrl+PageUp");

    // A code editor that has the keyboard keeps Ctrl+Tab for its files; Ctrl+PageDown still leaves it.
    await evaluate(`document.querySelector('#project-list button[title="Open the code editor"]').click()`);
    await until(`!!document.querySelector('.project-editor .editor-new-file')`, "the code editor of the project");
    for (let file = 0; file < 2; file++) await press(`document.querySelector('.project-editor .editor-new-file')`);
    const activeFile = `document.querySelector('.project-editor .editor-tab[aria-selected="true"] .editor-tab-name')?.textContent`;
    await until(`${activeFile}==='Untitled-2'`, "two new files in the editor");
    const front = `document.querySelector('.session-dock .flexlayout__tab_button--selected [data-session-node]')?.textContent.trim()`;
    const editor = await evaluate(front) as string;
    assert.match(editor, /^Editor/);
    await until(`!!document.querySelector('.project-editor .monaco-editor :is(.inputarea, .native-edit-context)')`, "the text of the editor");
    await evaluate(`document.querySelector('.project-editor .monaco-editor :is(.inputarea, .native-edit-context)').focus()`);
    await key("Tab");
    await until(`${activeFile}==='Untitled-1'`, "Ctrl+Tab in the editor shows its next file");
    assert.equal(await evaluate(front), editor, "the editor stays in front");
    await key("Tab", { shift: true });
    await until(`${activeFile}==='Untitled-2'`, "Ctrl+Shift+Tab in the editor shows its file before");
    assert.equal(await evaluate(front), editor, "the editor stays in front");
    const count = (await evaluate(tabs) as string[]).length;
    await key("PageDown");
    await until(`${front}==='one - Project'`, "Ctrl+PageDown from the editor goes around to the first tab of the window");
    assert.equal((await evaluate(tabs) as string[]).length, count, "no tab was opened or closed");
    assert.deepEqual(exceptions, []);
  });
});
