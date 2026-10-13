import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

// The window itself (main.tsx) over an isolated host. The keys scroll from what has the keyboard upwards, and the
// panel of a tab is focusable: the timeline of a session has to take the keyboard of a press in it, or the keys
// scroll nothing.
test("a press in the timeline of a session gives it the keyboard, not the panel of its tab", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true" }, async ({ evaluate, until, press, session, exceptions }) => {
    const timeline = `[...document.querySelectorAll('.session-dock .timeline-scroll')].find(view => view.offsetParent !== null && !view.classList.contains('new-session-welcome'))`;
    await until(`${session("one")}?.getAttribute('aria-pressed')==='true' && !!${timeline}`, "the session that is shown, with its timeline");
    const panel = await evaluate(`(() => { const panel = ${timeline}.closest('.flexlayout__tab'); return { role: panel?.getAttribute('role'), focusable: panel?.tabIndex }; })()`);
    assert.deepEqual(panel, { role: "tabpanel", focusable: -1 }, "the panel around the timeline can take a press");

    await press(timeline);
    assert.equal(await evaluate(`document.activeElement === ${timeline}`), true, "the timeline has the keyboard");
    assert.equal(await evaluate(`getComputedStyle(${timeline}).outlineStyle`), "none", "and shows no ring");
    // It is not a stop of the Tab key: the order of the stops is the one it was.
    assert.equal(await evaluate(`${timeline}.tabIndex`), -1);
    assert.deepEqual(exceptions, []);
  });
});
