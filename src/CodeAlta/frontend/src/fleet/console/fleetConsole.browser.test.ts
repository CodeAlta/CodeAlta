import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../../statistics/browserHarness";

const options = { entry: "../fleet/console/fleetConsole.mount.tsx", fixture: "fleetConsoleFixture", rpcFixture: "../demo-api.ts",
  styles: ["../machines/machines.css", "../fleet/fleet.css", "../spaces/spaces.css", "../fleet/console/fleetConsole.css"], width: 1600, height: 1000, ratio: 1.5,
  reducedMotion: true } as const;
const rows = `[...document.querySelectorAll('.fleet-table tbody tr[data-row-key]')]`;

test("the Overview of a fleet that runs says how it stands, what needs the person, and the server of each space", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-tiles')`, "the figures of the fleet");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-tile')].map(tile => tile.querySelector('.fleet-tile-value').textContent)`),
      ["17 / 22", "50", "Northwind Labs", "2 / 4"]);
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-attention li strong')].map(name => name.textContent)`),
      ["win-legacy", "mac-mini-qa", "perf-lab", "home-nas"], "the machine that refuses first, then those that do not answer; one still connecting is not counted");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-mini-table tbody tr')].map(row => row.textContent)`),
      ["DefaultThis computer and its machines", "WorkNorthwind LabsOnline", "Open sourceNorthwind LabsOnline", "PersonalThis computer and its machines"]);
    await page.shot("fleet-overview");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("with nothing set up, the Overview starts with a machine and a server, and what a server adds waits for it", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-tiles')`, "the fleet");
    await page.evaluate(`window.fleetConsoleFixture.start()`);
    await page.until(`document.querySelector('.fleet-onboarding')`, "the first steps");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-start-cards .fleet-step strong')].map(title => title.textContent)`),
      ["Add a machine", "Create or join a server"]);
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-step-grid .fleet-step')].map(step => [step.querySelector('strong').textContent, step.disabled])`),
      [["Invite people", true], ["Set policies", true], ["Share secrets", true], ["Set budgets", true]]);
    await page.shot("fleet-start");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a fleet that cannot be read says so, instead of offering to start one", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-tiles')`, "the fleet");
    await page.evaluate(`window.fleetConsoleFixture.fail()`);
    await page.until(`document.querySelector('.fleet-overview .bp6-non-ideal-state')?.textContent.includes('The fleet could not be read.')`, "the failure");
    assert.equal(await page.evaluate(`!!document.querySelector('.fleet-onboarding, .fleet-tiles')`), false, "neither the first steps nor figures that are not known");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-overview .bp6-callout')?.getAttribute('role')`), "alert");
  }, options);
});

test("a narrow window keeps the name and the state of each machine, and the header of the table in view", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.fleetConsoleFixture.show('machines')`);
    await page.until(`${rows}.length === 22`, "the table of machines");
    for (const [width, height] of [[1200, 850], [900, 650]]) {
      await page.resize(width, height);
      await page.until(`${rows}.every(row => row.cells[0].getBoundingClientRect().width >= 120 && row.querySelector('strong').getBoundingClientRect().width > 40)`, `the names at ${width}`);
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-table thead th')].filter(th => th.getBoundingClientRect().width > 0).map(th => th.textContent)`).then(names => (names as string[]).slice(0, 2)),
        ["Machine", "State"], `the first columns at ${width}`);
    }
    // The table scrolls in its frame: its header stays at the top of it.
    await page.evaluate(`document.querySelector('.fleet-table-frame').scrollTop = 400`);
    assert.ok(await page.evaluate(`Math.abs(document.querySelector('.fleet-table thead th').getBoundingClientRect().top - document.querySelector('.fleet-table-frame').getBoundingClientRect().top) < 2`), "the header in view");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the machines are filtered, sorted, and a row opens its details in place", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.fleetConsoleFixture.show('machines')`);
    await page.until(`${rows}.length === 22`, "the table of machines");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-filters-total').textContent`), "22 in all");
    // Sorted by state first: those online, then those connecting, offline, refused.
    assert.equal(await page.evaluate(`${rows}.at(-1).dataset.state`), "unauthorized");

    // Filter: the machines of macOS, by the menu of the systems.
    await page.clickText(".fleet-filters-facet", "System");
    await page.clickText(".fleet-filters-menu .bp6-menu-item .bp6-text-overflow-ellipsis", "macOS");
    await page.until(`${rows}.length === 4`, "the macs");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-filters-total').textContent`), "4 of 22");
    await page.key("Escape", 27);
    await page.clickText(".fleet-filters .bp6-button", "Clear");
    await page.until(`${rows}.length === 22`, "every machine again");

    // The search narrows by what a row says; a sort by name reads names as people do.
    await page.evaluate(`(() => { const input = document.querySelector('.fleet-filters-search input'); const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
      set.call(input, 'build-0'); input.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    await page.until(`${rows}.length === 4`, "the build machines");
    await page.clickText(".fleet-table-sort", "Machine");
    assert.deepEqual(await page.evaluate(`${rows}.map(row => row.querySelector('strong').textContent)`), ["build-01", "build-02", "build-03", "build-04"]);

    // A row opens its details under it, with the page of its machine.
    await page.click(".fleet-table tbody tr[data-row-key]");
    await page.until(`document.querySelector('.fleet-table-details .machine-row-details')`, "the details");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-table-details').previousElementSibling.getAttribute('aria-selected')`), "true");
    assert.ok(await page.evaluate(`[...document.querySelectorAll('.machine-details-actions .bp6-button')].some(button => button.textContent === 'Open its page')`));
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.fleet-table-details')`, "the details closed");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a large fleet shows its machines a page at a time, and the arrows and the details go past a page", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.fleetConsoleFixture.many(250); window.fleetConsoleFixture.show('machines')`);
    await page.until(`${rows}.length === 100`, "the first page");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-filters-total').textContent`), "250 in all", "the filters count every machine, drawn or not");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-table-more .muted-text').textContent`), "100 of 250");
    await page.clickText(".fleet-table-more .bp6-button", "Show 100 more");
    await page.until(`${rows}.length === 200`, "the second page");
    // The arrow past the last row drawn brings the next page, and the row below takes the focus.
    await page.evaluate(`${rows}[199].focus()`);
    await page.key("ArrowDown", 40);
    await page.until(`${rows}.length === 250 && document.activeElement === ${rows}[200]`, "the next page, and the focus on its first row");
    assert.equal(await page.evaluate(`!!document.querySelector('.fleet-table-more')`), false, "nothing more to show");

    // A narrower filter shows all it finds; a row opens its details whatever page it is on.
    await page.evaluate(`(() => { const input = document.querySelector('.fleet-filters-search input'); const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
      set.call(input, 'node-2'); input.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    await page.until(`${rows}.length === 51`, "node-2 and node-200 to node-250");
    await page.evaluate(`${rows}.at(-1).click()`);
    await page.until(`document.querySelector('.fleet-table-details .machine-row-details')`, "the details of the last row");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("back from the page of a machine, the table is as it was left, at that machine", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.fleetConsoleFixture.show('machines')`);
    await page.until(`${rows}.length === 22`, "the table of machines");
    await page.evaluate(`(() => { const input = document.querySelector('.fleet-filters-search input'); const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
      set.call(input, 'build-0'); input.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    await page.until(`${rows}.length === 4`, "the build machines");
    await page.clickText(".fleet-table-sort", "Machine");
    await page.click(".fleet-table tbody tr[data-row-key]", 2);
    await page.until(`document.querySelector('.fleet-table-details .machine-row-details')`, "the details of build-03");
    await page.clickText(".machine-details-actions .bp6-button", "Open its page");
    await page.until(`document.querySelector('.machine-page-back')`, "the page of the machine");
    await page.click(".machine-page-back");
    await page.until(`${rows}.length === 4`, "the table again");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-filters-search input').value`), "build-0", "its search");
    assert.deepEqual(await page.evaluate(`${rows}.map(row => row.querySelector('strong').textContent)`), ["build-01", "build-02", "build-03", "build-04"], "its sort");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-table-details').previousElementSibling.querySelector('strong').textContent`), "build-03", "its open row");
    assert.equal(await page.evaluate(`document.activeElement?.querySelector('strong')?.textContent`), "build-03", "the focus on the machine it comes back from");
  }, options);
});

test("the help opens the guide, and says first when the page has edits that are not saved", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.fleetConsoleFixture.show('machines')`);
    await page.until(`${rows}.length === 22`, "the table of machines");
    await page.click(".fleet-help-button");
    await page.clickText(".fleet-help .bp6-button", "Open the guide");
    await page.until(`window.fleetConsoleFixture.guides.length === 1`, "the guide");
    assert.deepEqual(await page.evaluate(`window.fleetConsoleFixture.guides`), ["fleet/machines.md"]);

    // In the window, the guide takes the place of Settings; here the help stays open, and Escape closes it.
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.fleet-help')`, "the help closed");
    await page.evaluate(`window.fleetConsoleFixture.unsaved(true)`);
    await page.click(".fleet-help-button");
    await page.clickText(".fleet-help .bp6-button", "Open the guide");
    await page.until(`document.querySelector('.fleet-help-warning')`, "the warning");
    assert.equal(await page.evaluate(`window.fleetConsoleFixture.guides.length`), 1, "nothing opens before the person says so");
    await page.clickText(".fleet-help .bp6-button", "Keep editing");
    await page.until(`!document.querySelector('.fleet-help')`, "the help closed, the edits kept");
    assert.equal(await page.evaluate(`window.fleetConsoleFixture.guides.length`), 1);
    await page.click(".fleet-help-button");
    await page.until(`document.querySelector('.fleet-help') && !document.querySelector('.fleet-help-warning')`, "the help again, without the warning");
    await page.clickText(".fleet-help .bp6-button", "Open the guide");
    await page.clickText(".fleet-help .bp6-button", "Open the guide anyway");
    await page.until(`window.fleetConsoleFixture.guides.length === 2`, "the guide, as the person chose");
  }, options);
});

test("the machines table is taken whole, with a row open, for the guide", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`window.fleetConsoleFixture.show('machines')`);
    await page.until(`${rows}.length === 22`, "the table of machines");
    await page.click(".fleet-table tbody tr[data-row-key]", 1);
    await page.until(`document.querySelector('.fleet-table-details .machine-row-details')`, "the details");
    await page.evaluate(`document.activeElement?.blur()`);
    await page.shot("fleet-machines");
    assert.deepEqual(page.problems, []);
  }, options);
});