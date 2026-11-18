import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";
import { testLimit } from "./testTime";

// The window itself (main.tsx) over an isolated host: the pane of a session that goes behind another tab stays laid out,
// out of the dock, so that showing it again lays nothing out. It is as hidden as a pane that is not laid out: nothing in
// it can be seen, pointed at or reached by the keyboard, and assistive technology does not read it.
test("the pane of a session behind another tab stays laid out, out of sight and out of reach", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true" }, async ({ command, evaluate, until, press, session, title, exceptions }) => {
    const pane = (name: string) => `[...document.querySelectorAll('.session-dock .flexlayout__tab')].find(pane => pane.getAttribute('aria-labelledby') === ${title(name)}.closest('[role="tab"]').id)`;
    const state = (name: string) => evaluate(`(() => { const pane = ${pane(name)}, box = pane.getBoundingClientRect(), dock = document.querySelector('.session-dock').getBoundingClientRect();
      const content = pane.querySelector('.session-tab-content');
      return { hiddenByDock: pane.style.display === 'none', laidOut: content.getClientRects().length > 0 && content.offsetWidth > 0, inDock: box.right > dock.left && box.left < dock.right,
        hiddenFromReaders: pane.getAttribute('aria-hidden') === 'true', heldWidth: content.style.width, focusInside: pane.contains(document.activeElement) }; })()`);
    const inPrompt = `!!document.activeElement?.closest('#session-prompt')`;

    await until(`!!${session("one")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await press(session("one"), 2);
    await until(inPrompt, "the prompt of session one has the keyboard");
    await press(session("two"), 2);
    await until(`!!${title("two - Project")} && ${inPrompt}`, "session two in front, its prompt with the keyboard");

    // Session one is hidden by the dock and still laid out, at the size it had, outside the dock.
    const hidden = await state("one - Project");
    assert.deepEqual({ ...hidden, heldWidth: /^\d+px$/.test(hidden.heldWidth), hiddenFromReaders: null },
      { hiddenByDock: true, laidOut: true, inDock: false, hiddenFromReaders: null, heldWidth: true, focusInside: false });
    assert.deepEqual({ ...await state("two - Project"), hiddenFromReaders: null },
      { hiddenByDock: false, laidOut: true, inDock: true, hiddenFromReaders: null, heldWidth: "", focusInside: true });
    // Nothing of it is under the pointer anywhere in the dock.
    assert.equal(await evaluate(`(() => { const dock = document.querySelector('.session-dock').getBoundingClientRect(), hidden = ${pane("one - Project")};
      for (let x = dock.left + 5; x < dock.right; x += 40) for (let y = dock.top + 5; y < dock.bottom; y += 40) if (hidden.contains(document.elementFromPoint(x, y))) return false;
      return true; })()`), true);

    // A moment later it is hidden from assistive technology too.
    await until(`${pane("one - Project")}.getAttribute('aria-hidden') === 'true'`, "the hidden pane is hidden from assistive technology");

    // Code that gives the keyboard to a control of the hidden pane does not take it from the prompt in front.
    await evaluate(`${pane("one - Project")}.querySelector('button:not(:disabled)').focus()`);
    assert.equal(await evaluate(inPrompt), true, "the keyboard stays in the prompt in front");

    // The Tab key goes around the window without ever entering the hidden pane, forward and backward.
    for (const modifiers of [0, 8]) {
      await evaluate(`document.querySelector('.session-dock .flexlayout__tab_button--selected').focus()`);
      for (let step = 0; step < 60; step++) {
        await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers });
        await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers });
        assert.equal(await evaluate(`${pane("one - Project")}.contains(document.activeElement)`), false, `Tab ${step + 1} (modifiers ${modifiers}) entered the hidden pane`);
      }
    }

    // Shown again, it is where the dock puts it, at the size of the dock, read again, with the keyboard in its prompt.
    await press(title("one - Project"));
    await until(`${pane("one - Project")}.style.display !== 'none' && ${inPrompt} && ${pane("one - Project")}.contains(document.activeElement)`, "session one in front again, its prompt with the keyboard");
    assert.deepEqual(await state("one - Project"),
      { hiddenByDock: false, laidOut: true, inDock: true, hiddenFromReaders: false, heldWidth: "", focusInside: true });
    assert.equal((await state("two - Project")).inDock, false, "session two is out of the dock in its turn");
    assert.deepEqual(exceptions, []);
  });
});
