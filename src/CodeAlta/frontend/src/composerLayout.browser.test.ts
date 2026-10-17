import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

// The window itself (main.tsx) over an isolated host, in a short window. The composer grows upwards over the timeline
// and never scrolls as a whole: its editor gives room back, so the toolbar of the prompt stays in the window, also
// when the splitter is dragged to the bottom. The entries of the timeline are centered like the prompt, and the button
// that jumps to the latest entry does not push the timeline under the divider (#174).
test("the prompt keeps its toolbar in a short window, and the timeline is centered above it", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true" }, async ({ command, evaluate, until, session, exceptions }) => {
    const workspace = `[...document.querySelectorAll('.session-dock .session-workspace')].find(view => view.querySelector('.session-timeline-area')?.offsetParent)`;
    const part = (selector: string) => `${workspace}?.querySelector(${JSON.stringify(selector)})`;
    await until(`${session("one")}?.getAttribute('aria-pressed')==='true' && !!${part(".prompt-surface #session-prompt")}`, "the session that is shown, with its prompt");
    await command("Emulation.setDeviceMetricsOverride", { width: 1500, height: 520, deviceScaleFactor: 1, mobile: false });
    const frames = () => evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(() => resolve(true), 50))))");
    type Box = { top: number; bottom: number; left: number; right: number };
    type Layout = { window: number; region: Box & { scroll: number; client: number; resized: boolean }; surface: Box; editor: Box; toolbar: Box; area: Box; splitter: Box;
      minimum: number; value: number };
    const layout = async () => {
      await frames();
      return await evaluate(`(() => {
        const box = element => { const value = element.getBoundingClientRect(); return { top: value.top, bottom: value.bottom, left: value.left, right: value.right }; };
        const region = ${part(".composer-region")}, splitter = ${part(".composer-splitter")};
        return { window: innerHeight, region: { ...box(region), scroll: region.scrollHeight, client: region.clientHeight, resized: region.classList.contains("resized") },
          surface: box(${part(".prompt-surface")}), editor: box(${part(".prompt-surface > .prompt-editor")}), toolbar: box(${part(".prompt-surface > .composer-toolbar")}),
          area: box(${part(".session-timeline-area")}), splitter: box(splitter),
          minimum: Number(splitter.getAttribute("aria-valuemin")), value: Number(splitter.getAttribute("aria-valuenow")) };
      })()`) as Layout;
    };
    const fits = (shown: Layout, what: string) => {
      assert.ok(shown.region.scroll <= shown.region.client + 1, `${what}: the composer does not scroll as a whole (${shown.region.scroll} > ${shown.region.client})`);
      assert.ok(shown.toolbar.bottom <= shown.surface.bottom && shown.surface.bottom <= shown.region.bottom + .5, `${what}: the toolbar is in the prompt, the prompt in its region`);
      assert.ok(shown.region.bottom <= shown.window + .5, `${what}: the composer is in the window`);
      assert.ok(shown.area.bottom <= shown.splitter.top + .5, `${what}: the timeline ends above the divider`);
    };

    // A prompt of many lines: the editor is at its largest, and the window has no room for it.
    await evaluate("document.querySelector('#session-prompt').focus()");
    await until("!!document.activeElement?.closest('#session-prompt')", "the editor has the keyboard");
    await command("Input.insertText", { text: Array.from({ length: 40 }, (_, line) => "line " + line).join("\n") });
    await until(`${part(".prompt-surface > .prompt-editor")}.offsetHeight > 200`, "the editor grows with its text");
    const tall = await layout();
    fits(tall, "a long prompt");
    assert.ok(tall.area.bottom - tall.area.top >= 28, "the timeline keeps some room above the prompt");

    // The splitter dragged to the bottom: the composer stops at its smallest layout, the editor two lines high.
    const x = (tall.splitter.left + tall.splitter.right) / 2, y = (tall.splitter.top + tall.splitter.bottom) / 2;
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x, y, button: "none", buttons: 0 });
    await command("Input.dispatchMouseEvent", { type: "mousePressed", x, y, button: "left", buttons: 1, clickCount: 1 });
    for (let step = 1; step <= 10; step++) await command("Input.dispatchMouseEvent", { type: "mouseMoved", x, y: y + step * 60, button: "left", buttons: 1 });
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", x, y: y + 600, button: "left", buttons: 0, clickCount: 1 });
    const low = await layout();
    assert.equal(low.region.resized, true, "the splitter gave the composer a height");
    fits(low, "a composer dragged to the bottom");
    assert.ok(Math.abs(low.value - low.minimum) <= 1, `the composer is at its least height (${low.value}, least ${low.minimum})`);
    assert.ok(Math.abs(low.editor.bottom - low.editor.top - 64) <= 1, "the editor gives room back down to two lines");

    // A timeline that does not follow its end any more, after a turn of the wheel: the button to the latest entry
    // shows over it, and the timeline still ends above the divider.
    await evaluate(`(() => { const spacer = document.createElement("div"); spacer.style.height = "3000px"; ${part(".timeline-scroll")}.append(spacer); })()`);
    const area = await layout();
    const wheel = { x: (area.area.left + area.area.right) / 2, y: (area.area.top + area.area.bottom) / 2 };
    for (let turn = 0; turn < 3; turn++) {
      await command("Input.dispatchMouseEvent", { type: "mouseWheel", ...wheel, deltaX: 0, deltaY: -400, button: "none", buttons: 0 });
      await frames();
    }
    await until(`!!${part(".timeline-bottom-button")}`, "the button to the latest entry");
    const away = await layout();
    fits(away, "a timeline with its button to the latest entry");
    const button = await evaluate(`(() => { const value = ${part(".timeline-bottom-button")}.getBoundingClientRect(); return { left: value.left, right: value.right, bottom: value.bottom }; })()`) as Box;
    assert.ok(button.bottom <= away.area.bottom, "the button is in the timeline");
    assert.ok(Math.abs((button.left + button.right) / 2 - (away.surface.left + away.surface.right) / 2) <= 1, "the button is centered like the prompt");

    // The entries are centered like the prompt, with the gutter of the scrollbar on the right: an entry as the
    // history shows it (the timeline of the isolated host has none).
    const entry = await evaluate(`(() => {
      const conversation = document.createElement("section"), messages = document.createElement("div"), message = document.createElement("article");
      conversation.className = "conversation history"; messages.className = "messages"; message.className = "message timeline-message message-assistant";
      message.textContent = "An entry"; messages.append(message); conversation.append(messages); ${part(".timeline-scroll")}.prepend(conversation);
      const view = ${part(".timeline-scroll")}; view.scrollTop = 0;
      const value = message.getBoundingClientRect();
      return { left: value.left, right: value.right, gutter: view.offsetWidth - view.clientWidth };
    })()`) as Box & { gutter: number };
    assert.ok(entry.gutter > 0, "the scrollbar has its gutter");
    assert.ok(Math.abs((entry.left - away.surface.left) - (away.surface.right - entry.right)) <= 1,
      `an entry is as far from both edges of the prompt (${entry.left - away.surface.left}, ${away.surface.right - entry.right})`);
    assert.deepEqual(exceptions, []);
  });
});
