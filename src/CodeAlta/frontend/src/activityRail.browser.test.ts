import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

const names = ["Explorer", "Search", "Automations", "Work items", "Issues", "Canvases", "Bookmarks", "Settings & extensions"];

// The window itself (main.tsx) over an isolated host: every button of the title bar's navigation can be seen and clicked,
// at the narrowest Explorer and without the Explorer. What has no room is in the "…" menu; the Explorer and Settings always show.
test("the buttons of the title bar stay reachable at the narrowest Explorer and without it", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", "codealta.desktop.ide-width.v1": JSON.stringify({ width: 220 }) }, async ({ command, evaluate, until, press, exceptions }) => {
    // Each button of the rail as the pointer finds it: the element at its center must be the button itself.
    const rail = () => evaluate(`[...document.querySelectorAll('.activity-rail > .bp6-button, .activity-rail > .bp6-popover-target > .bp6-button, .activity-rail > .activity-more')].map(button => {
      const r = button.getBoundingClientRect(), x = r.left + r.width / 2, y = r.top + r.height / 2;
      return { name: button.getAttribute('aria-label'), hit: !!document.elementFromPoint(x, y)?.closest('button') && document.elementFromPoint(x, y).closest('button') === button, width: Math.round(r.width) };
    })`) as Promise<{ name: string; hit: boolean; width: number }[]>;
    const toggle = `document.querySelector('.activity-rail button[aria-controls="project-rail"]')`;
    const menu = `[...document.querySelectorAll('.bp6-popover .bp6-menu-item')].map(item => item.textContent.trim())`;

    await until(`${toggle}?.getAttribute('aria-expanded')==='true' && Math.round(document.querySelector('.window-brand').getBoundingClientRect().width)===222`, "the Explorer at its narrowest width");
    let buttons = await rail();
    assert.ok(buttons.every(button => button.hit && button.width > 0), `every button shown can be clicked: ${JSON.stringify(buttons)}`);
    assert.equal(buttons[0].name, "Explorer");
    assert.equal(buttons.at(-1)!.name, "Settings & extensions", "Settings is always in the bar");
    assert.equal(buttons.at(-2)!.name, "More", "what has no room is in the menu");
    await press(`document.querySelector('.activity-rail .activity-more')`);
    await until(`${menu}.length > 0`, "the menu of the buttons that have no room");
    const folded = await evaluate(menu) as string[];
    assert.deepEqual([...buttons.slice(0, -2).map(button => button.name), ...folded, "Settings & extensions"], names, "each button is once in the bar or in the menu, in its order");
    // A line of the menu does what its button does.
    await evaluate(`[...document.querySelectorAll('.bp6-popover .bp6-menu-item')].find(item => item.textContent.trim() === 'Bookmarks').click()`);
    await until(`document.querySelector('.session-dock .flexlayout__tab_button--selected')?.textContent.includes('Bookmarks')`, "Bookmarks opened from the menu");
    await until(`!document.querySelector('.bp6-popover .bp6-menu')`, "the menu closed");
    assert.equal(await evaluate(`document.querySelector('.activity-rail .activity-more').classList.contains('bp6-active')`), true, "the menu button says a folded button is the tab in front");

    // Without the Explorer, every button has room in the bar.
    await evaluate(`${toggle}.click()`);
    await until(`${toggle}.getAttribute('aria-expanded')==='false' && !document.querySelector('.activity-rail .activity-more')`, "the Explorer hidden, and no menu");
    buttons = await rail();
    assert.deepEqual(buttons.map(button => button.name), names);
    assert.ok(buttons.every(button => button.hit), `every button can be clicked: ${JSON.stringify(buttons)}`);
    const brand = await evaluate(`(() => { const r = document.querySelector('.window-brand').getBoundingClientRect(), s = document.querySelector('.activity-settings').getBoundingClientRect(); return { right: r.right, settings: s.right }; })()`);
    assert.ok(brand.settings <= brand.right, `Settings ends inside the mark: ${JSON.stringify(brand)}`);

    // The keyboard still opens Settings.
    for (const type of ["rawKeyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: ",", code: "Comma", windowsVirtualKeyCode: 188, modifiers: 2 });
    await until(`!!document.querySelector('.settings-dialog-content')`, "Ctrl+, opens Settings");
    assert.deepEqual(exceptions, []);
  });
});
