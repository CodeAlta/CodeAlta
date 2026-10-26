import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

// The window itself (main.tsx) over an isolated host, in a web view without the CSS `app-region` (WebKitGTK): the
// desktop shell then moves the window on the mouse press of a title-bar drag region and cancels that press, and the
// compositor keeps the release. A press on the close button of a tab, or on the tab in front, must not reach it, or
// the tab is never closed nor shown.
test("a press on a tab of the title bar is not a press of the window's drag region", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true" }, async ({ evaluate, until, shown, press, session, title, exceptions }) => {
    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await press(session("two"), 2);
    await shown(["one - Project", "two - Project*"], "two tabs, the second in front");
    assert.equal(await evaluate(`document.querySelector('.session-dock .flexlayout__tab_button').closest('[data-neoastra-drag-region]') !== null`), true, "the tabs are in a drag region");

    // What the shell does where the view has no `app-region`: a mouse press in a drag region, outside what is
    // exempted from it, starts the move of the window and is cancelled. It counts them, and cancels the press.
    await evaluate(`(() => {
      CSS.supports = () => false;
      window.windowMoves = 0;
      document.addEventListener("mousedown", event => {
        const target = event.target;
        if (event.button !== 0 || !(target instanceof Element) || !target.closest("[data-neoastra-drag-region]")) return;
        if (target.closest("a,button,input,select,textarea,summary,label,[role=button],[contenteditable],[data-neoastra-no-drag]")) return;
        event.preventDefault(); window.windowMoves++;
      });
    })()`);
    const closeButton = (text: string) => `${title(text)}.closest('.flexlayout__tab_button').querySelector('.flexlayout__tab_button_trailing')`;

    // The tab in front is pressed: it has nothing to select, and its press is not the window's.
    await press(title("two - Project"));
    await shown(["one - Project", "two - Project*"], "the tab in front, still in front");
    assert.equal(await evaluate(`window.windowMoves`), 0, "a press on the tab in front does not move the window");
    // The tab behind is shown by its press.
    await press(title("one - Project"));
    await shown(["one - Project*", "two - Project"], "the first tab in front");
    assert.equal(await evaluate(`window.windowMoves`), 0, "a press on a tab behind does not move the window");
    // The close button of the tab in front closes it, and the one behind comes to the front.
    await press(closeButton("one - Project"));
    await shown(["two - Project*"], "the tab in front closed by its button");
    assert.equal(await evaluate(`window.windowMoves`), 0, "a press on a close button does not move the window");

    // The empty space of the bar is still the window's.
    await evaluate(`(() => {
      const bar = document.querySelector('.session-dock [data-neoastra-drag-region]'), tab = bar.querySelector('.flexlayout__tab_button').getBoundingClientRect(), box = bar.getBoundingClientRect();
      const x = Math.min(box.right - 4, tab.right + 200), y = tab.top + tab.height / 2;
      const under = document.elementFromPoint(x, y);
      under.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, cancelable: true, button: 0, clientX: x, clientY: y }));
    })()`);
    assert.equal(await evaluate(`window.windowMoves`), 1, "a press on the empty space of the bar moves the window");
    assert.deepEqual(exceptions, []);
  });
});
