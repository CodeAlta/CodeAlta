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
    // The focus ring of a button (2px, 2px away) is clipped by nothing under the mark, and the mark has room for it.
    const ringsShown = () => evaluate(`[...document.querySelectorAll('.activity-rail button')].filter(b => b.getBoundingClientRect().width > 0).every(b => {
      const brand = b.closest('.window-brand'), r = b.getBoundingClientRect(), box = brand.getBoundingClientRect();
      for (let e = b.parentElement; e !== brand; e = e.parentElement) if (getComputedStyle(e).overflow !== 'visible') return false;
      return r.left - 4 >= box.left && r.right + 4 <= box.right && r.top - 4 >= box.top && r.bottom + 4 <= box.bottom; })`) as Promise<boolean>;
    const menu = `[...document.querySelectorAll('.bp6-popover .bp6-menu-item')].map(item => item.textContent.trim())`;

    await until(`${toggle}?.getAttribute('aria-expanded')==='true' && Math.round(document.querySelector('.window-brand').getBoundingClientRect().width)===222`, "the Explorer at its narrowest width");
    let buttons = await rail();
    assert.ok(buttons.every(button => button.hit && button.width > 0), `every button shown can be clicked: ${JSON.stringify(buttons)}`);
    assert.equal(buttons[0].name, "Explorer");
    assert.equal(buttons.at(-1)!.name, "Settings & extensions", "Settings is always in the bar");
    assert.equal(buttons.at(-2)!.name, "More", "what has no room is in the menu");
    assert.equal(await ringsShown(), true, "the focus ring of every button shows whole at the narrowest Explorer");
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
    assert.equal(await ringsShown(), true, "the focus ring of every button shows whole without the Explorer");
    const brand = await evaluate(`(() => { const r = document.querySelector('.window-brand').getBoundingClientRect(), s = document.querySelector('.activity-settings').getBoundingClientRect(); return { right: r.right, settings: s.right }; })()`);
    assert.ok(brand.settings <= brand.right, `Settings ends inside the mark: ${JSON.stringify(brand)}`);

    // A very narrow window with the caption buttons of the system on the right: the buttons of the mark fold before they
    // reach the actions of the title bar, and every button shown, on either side, is the one the pointer finds.
    await evaluate(`document.documentElement.style.setProperty('--neoastra-titlebar-inset-right', '138px')`);
    const actions = `[...document.querySelectorAll('.window-actions > .bp6-button, .window-actions > .bp6-popover-target > .bp6-button')]`;
    for (const width of [620, 564, 520]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 700, deviceScaleFactor: 1, mobile: false });
      await until(`(() => { const brand = document.querySelector('.window-brand').getBoundingClientRect(); return ${actions}.every(button => button.getBoundingClientRect().left >= brand.right); })()`,
        `the mark ends before the actions of the title bar at ${width} px`);
      buttons = await rail();
      assert.ok(buttons.every(button => button.hit), `every button of the mark can be clicked at ${width} px: ${JSON.stringify(buttons)}`);
      assert.equal(buttons.at(-1)!.name, "Settings & extensions", `Settings is in the bar at ${width} px`);
      const right = await evaluate(`(() => { const box = document.querySelector('.window-actions').getBoundingClientRect();
        return ${actions}.map(button => { const r = button.getBoundingClientRect(), hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)?.closest('button');
          return { name: button.getAttribute('aria-label'), hit: hit === button, inside: r.left >= box.left - 0.5 && r.right <= box.right + 0.5, right: r.right }; }); })()`) as { name: string; hit: boolean; inside: boolean; right: number }[];
      assert.ok(right.length >= 4 && right.every(button => button.hit && button.inside && button.right <= width - 138),
        `every action of the title bar is in its box, before the caption buttons, and can be clicked at ${width} px: ${JSON.stringify(right)}`);
    }
    await evaluate(`document.documentElement.style.removeProperty('--neoastra-titlebar-inset-right')`);
    await command("Emulation.setDeviceMetricsOverride", { width: 1500, height: 900, deviceScaleFactor: 1, mobile: false });

    // The keyboard still opens Settings.
    for (const type of ["rawKeyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: ",", code: "Comma", windowsVirtualKeyCode: 188, modifiers: 2 });
    await until(`!!document.querySelector('.settings-dialog-content')`, "Ctrl+, opens Settings");
    assert.deepEqual(exceptions, []);
  });
});

// The dot of work items to do is drawn on the Work items button, or on the "…" button when Work items is folded into its menu,
// with the Explorer shown or hidden and in a narrow window: never on another button or elsewhere in the title bar.
test("the dot of a button of the title bar is on that button", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureWorkTodo: "true", "codealta.desktop.ide-width.v1": JSON.stringify({ width: 220 }) }, async ({ command, evaluate, until, exceptions }) => {
    const toggle = `document.querySelector('.activity-rail button[aria-controls="project-rail"]')`;
    const dots = () => evaluate(`[...document.querySelectorAll('.window-brand .activity-rail-dot')].map(dot => {
      const button = dot.closest('.bp6-button'), d = dot.getBoundingClientRect(), b = button.getBoundingClientRect();
      return { name: button.getAttribute('aria-label'), dot: [d.left, d.top, d.right, d.bottom].map(Math.round), button: [b.left, b.top, b.right, b.bottom].map(Math.round),
        inside: d.width > 0 && d.left >= b.left && d.right <= b.right && d.top >= b.top && d.bottom <= b.bottom,
        hit: document.elementFromPoint(d.left + d.width / 2, d.top + d.height / 2)?.closest('button') === button,
        expected: document.querySelector('.activity-rail > .activity-work') ? 'Work items' : 'More' };
    })`) as Promise<{ name: string; dot: number[]; button: number[]; inside: boolean; hit: boolean; expected: string }[]>;
    const seen = new Set<string>();
    const check = async (what: string) => {
      await until(`!!document.querySelector('.window-brand .activity-rail-dot')`, `the dot of work items to do ${what}`);
      const found = await dots();
      assert.equal(found.length, 1, `one dot in the title bar ${what}: ${JSON.stringify(found)}`);
      assert.equal(found[0].name, found[0].expected, `the dot is on the button of work items ${what}: ${JSON.stringify(found)}`);
      assert.ok(found[0].inside && found[0].hit, `the dot is drawn inside its button ${what}: ${JSON.stringify(found)}`);
      seen.add(found[0].name);
    };

    await until(`${toggle}?.getAttribute('aria-expanded')==='true' && Math.round(document.querySelector('.window-brand').getBoundingClientRect().width)===222`, "the Explorer at its narrowest width");
    await check("with the Explorer at its narrowest");
    await evaluate(`${toggle}.click()`);
    await until(`${toggle}.getAttribute('aria-expanded')==='false' && !document.querySelector('.activity-rail .activity-more')`, "the Explorer hidden, and no menu");
    await check("without the Explorer");
    assert.equal((await dots())[0].name, "Work items");

    // A narrow window: the buttons fold into the menu, and the dot follows the Work items button there.
    await evaluate(`document.documentElement.style.setProperty('--neoastra-titlebar-inset-right', '138px')`);
    const settled = `(() => { const brand = document.querySelector('.window-brand').getBoundingClientRect();
      return [...document.querySelectorAll('.window-actions > .bp6-button')].every(button => button.getBoundingClientRect().left >= brand.right); })()`;
    for (const width of [620, 520, 420]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 700, deviceScaleFactor: 1, mobile: false });
      await until(settled, `the mark ends before the actions of the title bar at ${width} px`);
      await check(`without the Explorer at ${width} px`);
    }
    await evaluate(`${toggle}.click()`);
    await until(`${toggle}.getAttribute('aria-expanded')==='true' && ${settled}`, "the Explorer shown again in a narrow window");
    await check("with the Explorer in a narrow window");
    assert.deepEqual([...seen].sort(), ["More", "Work items"], "the dot was seen on the Work items button and on the menu button");
    assert.deepEqual(exceptions, []);
  });
});
