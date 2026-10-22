import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

// The Issues tab in headless Edge, under the production content security policy and React StrictMode: a project
// whose issues are in several Jira projects lists them together, or one project alone.

const options = { entry: "../issues/issuesPanel.mount.tsx", fixture: "issuesFixture", rpcFixture: "../demo-api.ts", styles: ["../workItems/workItems.css", "../issues/issues.css"], width: 1300, height: 800 } as const;
const rows = `[...document.querySelectorAll('.issue-row-item .issue-ref')].map(reference => reference.textContent)`;
const part = `document.querySelector('.issues-part select')`;
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)}); select.value = ${JSON.stringify(value)}; select.dispatchEvent(new Event('change', { bubbles: true })); })()`;
const lastList = `(() => { const asked = issuesFixture.state.lists.at(-1); return [asked.service, asked.part]; })()`;

test("the issues of several Jira projects are listed together, and one project is listed alone when it is chosen", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    // GitHub is one place: it has no choice of a part.
    await page.until(`${rows}.join() === '#128'`, "the issues of the repository");
    assert.equal(await page.evaluate(`!!${part}`), false);

    await page.clickText('.issues-toolbar .bp6-segmented-control button', "Jira");
    await page.until(`${rows}.length === 5`, "the issues of the three Jira projects");
    assert.deepEqual(await page.evaluate(rows), ["PLAT-7", "ALTA-12", "TEAM-3", "ALTA-9", "PLAT-2"], "one list, in the order of the tracker");
    assert.deepEqual(await page.evaluate(`[...${part}.options].map(option => [option.value, option.textContent])`), [["", "All projects"], ["ALTA", "ALTA"], ["PLAT", "PLAT"], ["TEAM", "TEAM"]]);
    assert.deepEqual(await page.evaluate(`[${part}.value, ${part}.getAttribute('aria-label'), ${part}.title]`), ["", "Jira project", "ALTA, PLAT, TEAM"]);
    assert.deepEqual(await page.evaluate(lastList), ["jira", null]);

    // One project alone: the host is asked for it, and the count of the list follows.
    await page.evaluate(choose(".issues-part select", "PLAT"));
    await page.until(`${rows}.join() === 'PLAT-7,PLAT-2'`, "the issues of one Jira project");
    assert.deepEqual(await page.evaluate(lastList), ["jira", "PLAT"]);
    assert.equal(await page.evaluate(`document.querySelector('.work-stages .work-count').textContent`), "2");

    // The words that are typed are looked for in that project.
    await page.click('.issues-toolbar input[type="search"]');
    await page.type("cache");
    await page.until(`issuesFixture.state.lists.at(-1).query === 'cache'`, "the words that were typed");
    assert.deepEqual(await page.evaluate(lastList), ["jira", "PLAT"]);

    // A choice made for Jira says nothing of GitHub, and is found again with Jira.
    await page.clickText('.issues-toolbar .bp6-segmented-control button', "GitHub");
    await page.until(`!${part} && ${rows}.join() === '#128'`, "the repository again");
    assert.deepEqual(await page.evaluate(lastList), ["github", null]);
    await page.clickText('.issues-toolbar .bp6-segmented-control button', "Jira");
    await page.until(`${part}?.value === 'PLAT'`, "the Jira project that was chosen");
    await page.evaluate(choose(".issues-part select", ""));
    await page.until(`issuesFixture.state.lists.at(-1).part === null && issuesFixture.state.lists.at(-1).service === 'jira'`, "all the projects again");

    // A project the account does not see is named above the issues of the others.
    await page.evaluate(`issuesFixture.state.problem = 'Jira does not show the project GONE to this account: its issues are not listed.'`);
    await page.click('[aria-label="Reload"]');
    await page.until(`document.querySelector('.issues-notice')?.textContent.includes('GONE')`, "the project that is missing");
    assert.ok(await page.evaluate(`${rows}.length > 0`), "the others are still listed");
    assert.equal(await page.evaluate(`document.querySelector('.issues-notice').classList.contains('bp6-intent-warning')`), true, "a list that is incomplete is a warning, not a failure");

    // A project with one Jira project has nothing to choose: the tab names the project, as before.
    await page.evaluate(choose('.issues-toolbar select[aria-label="Project"]', "single"));
    await page.until(`${rows}.join() === 'DOCS-1'`, "the issues of the other project");
    assert.equal(await page.evaluate(`!!${part}`), false);
    assert.equal(await page.evaluate(`document.querySelector('.issues-source').textContent`), "JiraDOCS");
    assert.deepEqual(await page.evaluate(`[issuesFixture.state.lists.at(-1).projectId, issuesFixture.state.lists.at(-1).part]`), ["single", null]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("several issues start at once, each marked, and a session opens only while its issue is still read", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.clickText('.issues-toolbar .bp6-segmented-control button', "Jira");
    await page.until(`${rows}.length === 5`, "the issues of the Jira projects");
    const starting = `[...document.querySelectorAll('.issue-row-item')].filter(row => row.querySelector('.work-row-starting')).map(row => row.querySelector('.issue-ref').textContent)`;
    const startButton = `document.querySelector('.work-detail-actions .work-start-primary')`;

    // The first issue starts in its worktree; the next one can start meanwhile.
    await page.click('.work-detail-actions .work-start-primary');
    await page.until(`issuesFixture.state.starts.join() === 'PLAT-7'`, "the first start");
    assert.deepEqual(await page.evaluate(starting), ["PLAT-7"]);
    assert.equal(await page.evaluate(`${startButton}.disabled`), true, "the issue that starts cannot start twice");
    await page.click('.issue-row-item', 1);
    await page.until(`document.querySelector('.issue-detail h3')?.textContent.includes('ALTA-12')`, "the second issue read");
    await page.until(`${startButton} && !${startButton}.disabled`, "the start of the other issue");
    await page.click('.work-detail-actions .work-start-primary');
    await page.until(`issuesFixture.state.starts.join() === 'PLAT-7,ALTA-12'`, "the second start");
    assert.deepEqual(await page.evaluate(starting), ["PLAT-7", "ALTA-12"], "each issue that starts is marked in the list");

    // The first one ends while the second is read: its session is offered, not shown.
    await page.evaluate(`issuesFixture.state.finish['PLAT-7']('session-1')`);
    await page.until(`issuesFixture.state.offered.join() === 'session-1'`, "the session offered");
    assert.deepEqual(await page.evaluate(`issuesFixture.state.opened`), []);
    await page.until(`${starting}.join() === 'ALTA-12'`, "the mark of the issue that still starts");
    // The second one ends while it is read: its session is shown.
    await page.evaluate(`issuesFixture.state.finish['ALTA-12']('session-2')`);
    await page.until(`issuesFixture.state.opened.join() === 'session-2'`, "the session shown");
    await page.until(`${starting}.length === 0`, "no issue marked any more");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the choice of a Jira project is drawn in both themes and fits a narrow tab", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.clickText('.issues-toolbar .bp6-segmented-control button', "Jira");
    await page.until(`${rows}.length === 5`, "the issues of the Jira projects");
    const overflow = `(() => { const toolbar = document.querySelector('.issues-toolbar'); return toolbar.scrollWidth > toolbar.clientWidth || document.documentElement.scrollWidth > innerWidth; })()`;
    await page.shot("issues-jira-projects-dark");
    assert.equal(await page.evaluate(overflow), false);
    // The choice is as tall as the controls beside it.
    assert.ok(await page.evaluate(`Math.abs(${part}.getBoundingClientRect().height - document.querySelector('.issues-toolbar select[aria-label="Project"]').getBoundingClientRect().height) <= 1`));
    await page.evaluate(`document.documentElement.dataset.theme = 'light'`);
    await page.shot("issues-jira-projects-light");
    await page.resize(520, 700);
    await page.until(`innerWidth === 520`, "the narrow window");
    await page.shot("issues-jira-projects-narrow");
    assert.equal(await page.evaluate(overflow), false, "nothing is wider than a narrow tab");
    assert.ok(await page.evaluate(`(() => { const box = ${part}.getBoundingClientRect(); return box.width > 0 && box.right <= innerWidth; })()`));
    // In one column the list is as tall as its rows and the item that is read comes after it: the tab scrolls
    // them together, and nothing of the item is drawn over a row.
    const stacked = await page.evaluate<{ rows: number; lastRow: number; list: number; detail: number; scrolls: boolean }>(`(() => {
      const body = document.querySelector('.work-body'), all = [...document.querySelectorAll('.issue-row-item')];
      return { rows: all.length, lastRow: all.at(-1).getBoundingClientRect().bottom, list: document.querySelector('.work-list').getBoundingClientRect().bottom,
        detail: document.querySelector('.work-detail').getBoundingClientRect().top, scrolls: body.scrollHeight > body.clientHeight }; })()`);
    assert.equal(stacked.rows, 5);
    assert.ok(stacked.lastRow <= stacked.list + 1 && stacked.list <= stacked.detail + 1, `the item is read under the list, not over it: ${JSON.stringify(stacked)}`);
    assert.equal(stacked.scrolls, true, "what does not fit is reached by scrolling the tab");
    await page.evaluate(`document.querySelector('.work-body').scrollTo(0, 1e6)`);
    assert.ok(await page.evaluate(`(() => { const body = document.querySelector('.work-body').getBoundingClientRect(), text = document.querySelector('.work-detail-text').getBoundingClientRect(); return text.bottom <= body.bottom + 1; })()`),
      "the end of the description is reached");
  }, options);
});

test("the toolbar switches between the views of a project, the line under it changes what a view lets change, and a window of the tab manages them", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    const asked = `JSON.stringify(issuesFixture.state.lists.at(-1).filters)`;
    // The line under the toolbar: the filters the tab may change with their value, then the count of the others, which its label names.
    const active = `[[...document.querySelectorAll('.issues-active-filters .issues-pick')].map(item => item.querySelector('strong').textContent + ' ' + item.querySelector('button').textContent),
      document.querySelector('.issues-active-fixed')?.textContent ?? null, document.querySelector('.issues-active-fixed')?.getAttribute('aria-label') ?? null]`;
    const view = `document.querySelector('.issues-views select')`;
    const rowsOfWindow = `[...document.querySelectorAll('.issue-views-window .issue-views-table tbody tr')].map(row => [row.querySelector('code').textContent,
      row.querySelector('.issue-views-active input').checked, row.querySelector('.issue-views-pick input').checked])`;
    const pickFromMenu = async (text: string) => {
      await page.click('.issues-pick-choice');
      await page.until(`!!document.querySelector('.issues-pick-menu')`, "the values of the filter");
      await page.evaluate(`[...document.querySelectorAll('.issues-pick-menu .bp6-menu-item')].find(item => item.querySelector('.bp6-text-overflow-ellipsis')?.textContent === ${JSON.stringify(text)}).click()`);
      await page.until(`!document.querySelector('.issues-pick-menu')`, "the menu closed");
    };
    const save = async (count: number, what: string) => {
      await page.clickText('.issue-views-window .issue-views-buttons button', "Save");
      await page.until(`issuesFixture.state.saved.length === ${count}`, what);
    };
    // GitHub offers no filter: the toolbar is as before.
    await page.until(`${rows}.join() === '#128'`, "the issues of the repository");
    assert.equal(await page.evaluate(`!!document.querySelector('.issues-views') || !!document.querySelector('.issues-active-filters')`), false);

    // The view of the team is the current one: the tab lists by its filters, the one it may change first, with its values.
    await page.evaluate(choose('.issues-toolbar select[aria-label="Project"]', "ado"));
    await page.until(`${rows}.join() === '#41'`, "the work items of the view of the team");
    assert.deepEqual(await page.evaluate(`[...${view}.querySelectorAll('optgroup')].map(group => [group.label, [...group.querySelectorAll('option')].map(option => option.textContent)])`),
      [["For me", ["Bugs"]], ["For the team", ["Sprint board"]]]);
    assert.equal(await page.evaluate(`${view}.selectedOptions[0].textContent`), "Sprint board");
    assert.deepEqual(await page.evaluate(active), [["Sprint: Current sprint"], "+ 2 filters", "2 more filters. Team: Platform. Type: Task"]);
    // The other filters are named on hover.
    const [x, y] = await page.evaluate<number[]>(`(() => { const box = document.querySelector('.issues-active-fixed').getBoundingClientRect(); return [box.x + box.width / 2, box.y + box.height / 2]; })()`);
    await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x, y });
    await page.until(`!!document.querySelector('.issues-fixed-list')`, "the filters of the view, on hover");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.issues-fixed-list .issues-active-filter')].map(item => item.textContent)`), ["Team: Platform", "Type: Task"]);
    await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 5, y: 5 });
    await page.until(`!document.querySelector('.issues-fixed-list')`, "the list gone");
    assert.equal(await page.evaluate(asked), JSON.stringify({ sprint: "@current", team: "t1", type: "Task" }));
    assert.equal(await page.evaluate(`issuesFixture.state.lists.filter(list => list.projectId === 'ado').length`), 1, "the project is not listed whole before its filters are known");
    assert.equal(await page.evaluate(`document.querySelectorAll('.issues-toolbar .issues-pick').length`), 0, "the toolbar has no filter of its own");

    // The sprint is changed in the line for a while; its menu says what more each value is. The view keeps its value, and the reset gives it back.
    await page.click('.issues-pick-choice');
    await page.until(`!!document.querySelector('.issues-pick-menu')`, "the sprints");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.issues-pick-menu .bp6-menu-item')].map(item => [item.querySelector('.bp6-text-overflow-ellipsis').textContent,
      item.querySelector('.bp6-menu-item-label')?.textContent ?? null, (item.closest('[aria-selected]') ?? item.querySelector('[aria-selected]'))?.getAttribute('aria-selected')])`),
      [["All", null, "false"], ["Current sprint", "S12 of t1", "true"], ["S11 of t1", null, "false"]]);
    await page.click('.issues-pick-choice');
    await page.until(`!document.querySelector('.issues-pick-menu')`, "the menu closed");
    await pickFromMenu("S11 of t1");
    await page.until(`${asked} === JSON.stringify({ sprint: "s-t1", team: "t1", type: "Task" })`, "the sprint chosen in the tab");
    assert.deepEqual(await page.evaluate(`JSON.parse(localStorage.getItem("codealta.desktop.issue-picks.v1"))["ado\\nazure_devops\\nteam\\nSprint board"]`), { sprint: "s-t1" });
    assert.equal(await page.evaluate(`issuesFixture.state.saved.length`), 0, "the view is not written");
    await pickFromMenu("All");
    await page.until(`${asked} === JSON.stringify({ sprint: "", team: "t1", type: "Task" })`, "every sprint, asked as such");
    await page.click('.issues-pick-reset');
    await page.until(`${asked} === JSON.stringify({ sprint: "@current", team: "t1", type: "Task" })`, "the sprint of the view again");
    assert.equal(await page.evaluate(`!!document.querySelector('.issues-pick-reset')`), false);

    // Another view: the tab lists by its filters.
    await page.evaluate(choose('.issues-views select', "personal\nBugs"));
    await page.until(`issuesFixture.state.selects.at(-1) === "Bugs"`, "the view of the user, chosen");
    await page.until(`${asked} === JSON.stringify({ type: "Bug" })`, "the work items of the view of the user");
    assert.deepEqual(await page.evaluate(active), [[], "1 filter", "1 more filter. Type: Bug"]);

    // The line of the filters opens the window of the views, on the current one: it lists the filters of the view only.
    await page.click('.issues-active-fixed');
    await page.until(`!!document.querySelector('.issue-views-window .issue-views-table tbody tr')`, "the window of the views");
    assert.deepEqual(await page.evaluate(`[document.getElementById(document.querySelector('.issue-views-window').getAttribute('aria-labelledby'))?.textContent,
      document.querySelector('#issue-views-view').selectedOptions[0].textContent, ${rowsOfWindow},
      [...document.querySelectorAll('.issue-views-window tbody tr [aria-label^="Remove "]')].length]`),
      ["Views of Platform", "Bugs", [["type", true, false]], 1], "every row can be removed");

    // A filter of the tracker's own is added, given a value, offered in the tab, and listed by once saved.
    await page.clickText('.issue-views-window .issue-views-buttons button', "Add a filter");
    await page.until(`!!document.querySelector('.issue-views-fields .bp6-menu-item')`, "the filters that can be added");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.issue-views-fields .bp6-menu-item .bp6-text-overflow-ellipsis')].map(item => item.textContent)`).then(items => (items as string[]).slice(0, 4)),
      ["Team", "Sprint", "Order", "Priority"], "the tracker's own first, then the fields");
    await page.evaluate(`[...document.querySelectorAll('.issue-views-fields .bp6-menu-item')].find(item => item.querySelector('.bp6-text-overflow-ellipsis')?.textContent === "Team").click()`);
    await page.until(`document.querySelector('.issue-views-window select[aria-label="Value of Team"]')?.disabled === false`, "the team, added and on");
    await page.evaluate(choose('.issue-views-window select[aria-label="Value of Team"]', "t2"));
    await page.click('.issue-views-window input[aria-label="Change Team in the tab"]');
    await save(1, "the view saved from the window");
    assert.deepEqual(await page.evaluate(`(({ source, name, select, filters }) => ({ source, name, select, filters }))(issuesFixture.state.saved[0])`), { source: "personal", name: "Bugs", select: false,
      filters: [{ id: "type", mode: "on", value: "Bug", label: null, pick: null }, { id: "team", mode: "on", value: "Mobile", label: null, pick: true }] });
    await page.until(`${rows}.join() === '#42'`, "the tab lists by the view as it is now");
    assert.deepEqual(await page.evaluate(active), [["Team: Mobile"], "+ 1 filter", "1 more filter. Type: Bug"], "the filter the tab may change, then the others counted");

    // Turned off, a filter keeps its value in the view; removed, it leaves the view.
    await page.click('.issue-views-window input[aria-label="List by Team"]');
    await save(2, "the filter turned off");
    assert.deepEqual(await page.evaluate(`issuesFixture.state.saved[1].filters[1]`), { id: "team", mode: "off", value: "Mobile", label: null, pick: true });
    await page.until(`${asked} === JSON.stringify({ type: "Bug" })`, "the team, no longer listed by");
    await page.click('.issue-views-window [aria-label="Remove Team from this view"]');
    await save(3, "the filter removed");
    assert.deepEqual(await page.evaluate(`issuesFixture.state.saved[2].filters`), [{ id: "type", mode: "on", value: "Bug", label: null, pick: null }]);
    await page.click('.issue-views-window [aria-label="Close"]');
    await page.until(`!document.querySelector('.issue-views-window')`, "the window closed");

    // The gear beside the views opens the window too.
    await page.click('.issues-views-manage');
    await page.until(`!!document.querySelector('.issue-views-window')`, "the window, from the gear");
    await page.click('.issue-views-window [aria-label="Close"]');
    await page.until(`!document.querySelector('.issue-views-window')`, "the window closed again");

    // In a wide window the content of the tab is centred: the line of the filters stays under the toolbar.
    await page.resize(2200, 900);
    await page.until(`document.querySelector('.issues-toolbar').getBoundingClientRect().left > 100`, "the content, centred");
    assert.equal(await page.evaluate(`Math.round(document.querySelector('.issues-active-filters').getBoundingClientRect().left - document.querySelector('.issues-toolbar').getBoundingClientRect().left)`), 0);

    // Pull requests have no filters.
    await page.clickText('.issues-toolbar .bp6-segmented-control button', "Pull requests");
    await page.until(`issuesFixture.state.lists.at(-1).kind === 'pull_request'`, "the pull requests");
    assert.equal(await page.evaluate(`[document.querySelectorAll('.issues-views, .issues-active-filters').length, issuesFixture.state.lists.at(-1).filters]`).then(JSON.stringify), JSON.stringify([0, null]));
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a project without a view lists every item, and the gear in the toolbar makes its first view", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    // The project has no view yet: nothing in its files.
    await page.evaluate(`issuesFixture.state.views = []; issuesFixture.state.current = null`);
    await page.until(`${rows}.join() === '#128'`, "the issues of the repository");
    await page.evaluate(choose('.issues-toolbar select[aria-label="Project"]', "ado"));
    await page.until(`issuesFixture.state.lists.some(list => list.projectId === 'ado')`, "the work items of the project");
    assert.deepEqual(await page.evaluate(`[document.querySelectorAll('.issues-views select').length, document.querySelector('.issues-views-manage')?.textContent,
      document.querySelectorAll('.issues-active-filters').length, issuesFixture.state.lists.at(-1).filters]`),
      [0, "Manage views…", 0, null], "no list of views: the gear says what it does, and nothing narrows the list");

    // The window says there is no view, and the first one is the user's, named Mine.
    await page.click('.issues-views-manage');
    await page.until(`!!document.querySelector('.issue-views-window .bp6-callout')`, "the window without a view");
    assert.equal(await page.evaluate(`document.querySelector('.issue-views-window .bp6-callout').textContent`),
      "This project has no view yet: the Issues tab lists every item. A new view says which filters it shows.");
    await page.clickText('.issue-views-window .issue-views-actions button', "New view");
    await page.until(`document.querySelector('#issue-views-new-name')?.value === "Mine"`, "the name of the first view");
    await page.clickText('.issue-views-popover button', "Create");
    await page.until(`issuesFixture.state.saved.length === 1`, "the first view");
    assert.deepEqual(await page.evaluate(`(({ source, name, select, filters }) => ({ source, name, select, filters }))(issuesFixture.state.saved[0])`),
      { source: "personal", name: "Mine", select: true, filters: [] });
    await page.until(`!!document.querySelector('.issue-views-window .issue-views-empty')`, "the new view, without filters");
    await page.click('.issue-views-window [aria-label="Close"]');
    await page.until(`!document.querySelector('.issue-views-window')`, "the window closed");

    // The tab now has a list of views, and the gear alone.
    await page.until(`document.querySelector('.issues-views select')?.selectedOptions[0]?.textContent === "Mine"`, "the list of views");
    assert.equal(await page.evaluate(`document.querySelector('.issues-views-manage').textContent`), "");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the window of the views keeps what is edited while the tab renders again", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`${rows}.join() === '#128'`, "the issues of the repository");
    await page.evaluate(choose('.issues-toolbar select[aria-label="Project"]', "ado"));
    await page.until(`${rows}.join() === '#41'`, "the work items of the view of the team");
    await page.click('.issues-views-manage');
    await page.until(`!!document.querySelector('.issue-views-window input[aria-label="List by Type"]')`, "the window of the views");
    const reads = await page.evaluate<number>(`issuesFixture.state.viewReads`);

    // A filter is turned off and not saved; the shell renders the tab again on its own events.
    await page.click('.issue-views-window input[aria-label="List by Type"]');
    await page.until(`document.querySelector('.issue-views-window input[aria-label="List by Type"]').checked === false`, "the filter turned off");
    for (let index = 0; index < 3; index++) await page.evaluate(`issuesFixture.rerender()`);
    await page.evaluate(`new Promise(resolve => setTimeout(resolve, 300))`);
    assert.equal(await page.evaluate(`issuesFixture.state.viewReads`), reads, "the views are not read again");
    assert.equal(await page.evaluate(`document.querySelector('.issue-views-window input[aria-label="List by Type"]')?.checked`), false, "what was edited is still there");
    assert.equal(await page.evaluate(`issuesFixture.state.saved.length`), 0);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("values the host refuses or fails on are not asked again and again, and refused ones are forgotten", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    const key = "ado\\nazure_devops\\nteam\\nSprint board";
    const stored = `JSON.parse(localStorage.getItem("codealta.desktop.issue-picks.v1") ?? "{}")["${key}"] ?? null`;
    const asks = `issuesFixture.state.filterAsks.filter(ask => ask.projectId === 'ado' && !ask.withoutView).length`;
    const steady = async (what: string) => {
      const before = await page.evaluate<number>(asks);
      await page.evaluate(`new Promise(resolve => setTimeout(resolve, 500))`);
      assert.equal(await page.evaluate<number>(asks), before, `the filters are not asked again: ${what}`);
    };
    // A value kept from before that the host refuses (longer than it takes): it is forgotten, and the view's own is listed by.
    await page.evaluate(`localStorage.setItem("codealta.desktop.issue-picks.v1", JSON.stringify({ "${key}": { sprint: "x".repeat(200) } }))`);
    await page.until(`${rows}.join() === '#128'`, "the issues of the repository");
    await page.evaluate(choose('.issues-toolbar select[aria-label="Project"]', "ado"));
    await page.until(`${rows}.join() === '#41'`, "the work items of the view of the team");
    await page.until(`${stored} === null`, "the refused value forgotten");
    await steady("after a refusal");
    assert.equal(await page.evaluate(`JSON.stringify(issuesFixture.state.lists.at(-1).filters)`), JSON.stringify({ sprint: "@current", team: "t1", type: "Task" }));

    // The tracker fails on a value chosen in the tab: the filters are not asked in a loop, the list is not left waiting,
    // and the value can be changed back.
    await page.evaluate(`issuesFixture.state.failPicks = true`);
    const listed = await page.evaluate<number>(`issuesFixture.state.lists.length`);
    await page.click('.issues-pick-choice');
    await page.until(`!!document.querySelector('.issues-pick-menu')`, "the sprints");
    await page.evaluate(`[...document.querySelectorAll('.issues-pick-menu .bp6-menu-item')].find(item => item.querySelector('.bp6-text-overflow-ellipsis')?.textContent === "S11 of t1").click()`);
    await page.until(`issuesFixture.state.filterAsks.at(-1).selections?.sprint === 's-t1'`, "the sprint asked");
    await page.until(`issuesFixture.state.lists.length > ${listed} && ${rows}.join() === '#41' && !document.querySelector('.work-list .work-empty')`, "the list, not left waiting");
    await steady("after a failure");
    assert.deepEqual(await page.evaluate(stored), { sprint: "s-t1" }, "a value the tracker failed on is kept: it may work again");
    assert.equal(await page.evaluate(`document.querySelector('.issues-pick-choice')?.textContent`), "S11 of t1");
    await page.evaluate(`issuesFixture.state.failPicks = false`);
    await page.click('.issues-pick-reset');
    await page.until(`JSON.stringify(issuesFixture.state.lists.at(-1).filters) === JSON.stringify({ sprint: "@current", team: "t1", type: "Task" })`, "the sprint of the view again");
    assert.equal(await page.evaluate(stored), null);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("quick clicks in a filter of several values keep each value, and a text filter takes what the host takes", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    // A view of the user lets the tab change the states and the customer.
    await page.evaluate(`issuesFixture.state.views.push({ name: "Triage", source: "personal", path: ".alta/local/issues.toml", filters: [
      { id: "field:System.State", mode: "on", value: "New", label: null, pick: true }, { id: "field:Custom.Customer", mode: "on", value: null, label: null, pick: true }] });
      issuesFixture.state.current = { name: "Triage", source: "personal" }`);
    await page.until(`${rows}.join() === '#128'`, "the issues of the repository");
    await page.evaluate(choose('.issues-toolbar select[aria-label="Project"]', "ado"));
    await page.until(`!!document.querySelector('.issues-active-filters .issues-multi')`, "the states, in the line of the filters");
    assert.equal(await page.evaluate(`document.querySelector('.issues-active-filters .issues-filter-text input').maxLength`), 128);

    // Two states are ticked before the host answers the first: both are asked, with the one of the view.
    await page.evaluate(`issuesFixture.state.paused = true`);
    await page.click('.issues-active-filters .issues-multi');
    await page.until(`!!document.querySelector('.issues-multi-popover')`, "the states");
    const ticked = `Object.fromEntries([...document.querySelectorAll('.issues-multi-popover label.bp6-checkbox')].map(item => [item.textContent, item.querySelector('input').checked]))`;
    const tick = (label: string) => `[...document.querySelectorAll('.issues-multi-popover label.bp6-checkbox')].find(item => item.textContent === ${JSON.stringify(label)}).querySelector('input').click()`;
    await page.evaluate(tick("Ready"));
    await page.until(`${ticked}["Ready"] === true`, "Ready ticked");
    await page.evaluate(tick("On Hold"));
    assert.deepEqual(await page.evaluate(ticked), { "New": true, "Ready": true, "On Hold": true }, "the state ticked first is still ticked");
    await page.evaluate(`issuesFixture.state.paused = false; issuesFixture.state.waiting.splice(0).forEach(resume => resume())`);
    await page.until(`issuesFixture.state.lists.at(-1).filters?.["field:System.State"] === "New\\u001fReady\\u001fOn Hold"`, "the three states listed by");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a tracker that fails on the filters leaves the tab listing by those it shows, and says why", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    const listed = `JSON.stringify(issuesFixture.state.lists.at(-1).filters)`;
    const notice = `[...document.querySelectorAll('.issues-notice')].map(item => item.textContent)`;
    await page.until(`${rows}.join() === '#128'`, "the issues of the repository");
    await page.evaluate(choose('.issues-toolbar select[aria-label="Project"]', "ado"));
    await page.until(`${rows}.join() === '#41' && ${listed} === JSON.stringify({ sprint: "@current", team: "t1", type: "Task" })`, "the work items of the view of the team");

    // The tracker answers without its filters for the sprint chosen: the tab lists by that sprint, as it shows it, not by the view's current one.
    await page.evaluate(`issuesFixture.state.failPicks = true`);
    await page.click('.issues-pick-choice');
    await page.until(`!!document.querySelector('.issues-pick-menu')`, "the sprints");
    await page.evaluate(`[...document.querySelectorAll('.issues-pick-menu .bp6-menu-item')].find(item => item.querySelector('.bp6-text-overflow-ellipsis')?.textContent === "S11 of t1").click()`);
    await page.until(`${notice}.includes("Azure DevOps did not give its filters.")`, "the problem of the tracker");
    await page.until(`${listed} === JSON.stringify({ sprint: "s-t1", team: "t1", type: "Task" })`, "the list asked for the sprint shown");
    assert.equal(await page.evaluate(`document.querySelector('.issues-pick-choice')?.textContent`), "S11 of t1");

    // A request of the filters that fails is said too, and the list is still asked for what is shown.
    await page.evaluate(`issuesFixture.state.failPicks = false; issuesFixture.state.throwFilters = true`);
    const lists = await page.evaluate<number>(`issuesFixture.state.lists.length`);
    await page.click('[aria-label="Reload"]');
    await page.until(`${notice}.includes("The filters could not be read.") && issuesFixture.state.lists.length > ${lists}`, "the failure of the request, and the list read again");
    assert.equal(await page.evaluate(listed), JSON.stringify({ sprint: "s-t1", team: "t1", type: "Task" }));
    assert.deepEqual(await page.evaluate(`issuesFixture.state.lists.slice(${lists}).map(list => JSON.stringify(list.filters))`).then(all => [...new Set(all as string[])]),
      [JSON.stringify({ sprint: "s-t1", team: "t1", type: "Task" })], "never every item, nor the values of the view");

    // Once the filters are read again, the warning goes.
    await page.evaluate(`issuesFixture.state.throwFilters = false`);
    await page.click('[aria-label="Reload"]');
    await page.until(`${notice}.length === 0`, "no warning any more");
    assert.equal(await page.evaluate(listed), JSON.stringify({ sprint: "s-t1", team: "t1", type: "Task" }));
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a view chosen, made, renamed or removed starts anew: nothing of the view before is shown or asked with when its filters fail", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    const tabAsks = `issuesFixture.state.filterAsks.filter(ask => ask.projectId === 'ado' && !ask.withoutView)`;
    const notice = `[...document.querySelectorAll('.issues-notice')].map(item => item.textContent)`;
    const stored = `JSON.parse(localStorage.getItem("codealta.desktop.issue-picks.v1") ?? "{}")`;
    const viewOptions = `[...document.querySelectorAll('.issues-views select option')].map(option => option.textContent)`;
    const pickSprint = async (text: string) => {
      await page.click('.issues-pick-choice');
      await page.until(`!!document.querySelector('.issues-pick-menu')`, "the sprints");
      await page.evaluate(`[...document.querySelectorAll('.issues-pick-menu .bp6-menu-item')].find(item => item.querySelector('.bp6-text-overflow-ellipsis')?.textContent === ${JSON.stringify(text)}).click()`);
      await page.until(`${tabAsks}.at(-1).selections?.sprint === 's-t1' && JSON.stringify(issuesFixture.state.lists.at(-1).filters).includes('s-t1')`, "the sprint chosen, listed by");
    };
    // A change that succeeds, then the read of the filters that follows fails: every ask after it is made without the values of the view before.
    const failedAfter = async (what: string, change: () => Promise<void>) => {
      const before = await page.evaluate<number>(`${tabAsks}.length`);
      await page.evaluate(`issuesFixture.state.failFilters = true`);
      await change();
      await page.until(`${tabAsks}.length > ${before} && ${notice}.includes("Azure DevOps did not give its filters.")`, `the failed read after ${what}`);
      assert.deepEqual(await page.evaluate(`${tabAsks}.slice(${before}).map(ask => ask.selections)`).then(all => [...new Set((all as object[]).map(value => JSON.stringify(value)))]),
        ["{}"], `nothing of the view before is asked with after ${what}`);
      assert.equal(await page.evaluate(`!!document.querySelector('.issues-pick-choice')`), false, `the filters of the view before are not shown after ${what}`);
      await page.until(`issuesFixture.state.lists.at(-1).filters === null`, `the host lists by the view after ${what}`);
    };
    const recover = async (view: string) => {
      await page.evaluate(`issuesFixture.state.failFilters = false`);
      await page.click('[aria-label="Reload"]');
      await page.until(`document.querySelector('.issues-views select')?.selectedOptions[0]?.textContent === ${JSON.stringify(view)} && ${notice}.length === 0`, `the view ${view}`);
    };
    const openWindow = async () => {
      await page.click('.issues-views-manage');
      await page.until(`!!document.querySelector('.issue-views-window .issue-views-table')`, "the window of the views");
    };
    const closeWindow = async () => {
      await page.click('.issue-views-window [aria-label="Close"]');
      await page.until(`!document.querySelector('.issue-views-window')`, "the window closed");
    };
    await page.until(`${rows}.join() === '#128'`, "the issues of the repository");
    await page.evaluate(choose('.issues-toolbar select[aria-label="Project"]', "ado"));
    await page.until(`${rows}.join() === '#41'`, "the work items of the view of the team");

    // Chosen in the toolbar.
    await pickSprint("S11 of t1");
    await failedAfter("a view chosen", async () => {
      await page.evaluate(choose('.issues-views select', "personal\nBugs"));
      await page.until(`issuesFixture.state.selects.at(-1) === "Bugs"`, "the view of the user, chosen");
    });
    assert.notEqual(await page.evaluate(`document.querySelector('.issues-views select')?.value ?? null`), "team\nSprint board", "the view before is not shown as the current one");
    assert.deepEqual(await page.evaluate(`${stored}["ado\\nazure_devops\\nteam\\nSprint board"]`), { sprint: "s-t1" }, "the values of the view before stay its own");
    await recover("Bugs");
    await page.evaluate(choose('.issues-views select', "team\nSprint board"));
    await page.until(`document.querySelector('.issues-pick-choice')?.textContent === "S11 of t1"`, "the view of the team again, with its sprint");

    // Made in the window, from the view of the team: the window stays.
    await openWindow();
    await page.clickText('.issue-views-window .issue-views-actions button', "New view");
    await page.until(`!!document.querySelector('#issue-views-new-name')`, "the name of the new view");
    await page.evaluate(`(() => { const input = document.querySelector('#issue-views-new-name'); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, 'Mine'); input.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    await failedAfter("a view made", async () => {
      await page.clickText('.issue-views-popover button', "Create");
      await page.until(`issuesFixture.state.saved.length === 1`, "the view made");
    });
    assert.equal(await page.evaluate(`!!document.querySelector('.issue-views-window')`), true, "the window stays open");
    await closeWindow();
    await recover("Mine");

    // Renamed in the window: the values chosen follow the new name, and the old one is not shown.
    await pickSprint("S11 of t1");
    await openWindow();
    await page.clickText('.issue-views-window .issue-views-actions button', "Rename");
    await page.until(`!!document.querySelector('.issue-views-popover input')`, "the new name");
    await page.evaluate(`(() => { const input = document.querySelector('.issue-views-popover input'); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, 'Ours'); input.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    await failedAfter("a view renamed", async () => {
      await page.clickText('.issue-views-popover button', "Rename");
      await page.until(`issuesFixture.state.current?.name === "Ours"`, "the view renamed");
    });
    assert.equal(await page.evaluate(`${viewOptions}.includes("Mine")`), false, "the old name is not shown");
    assert.deepEqual(await page.evaluate(`[${stored}["ado\\nazure_devops\\npersonal\\nMine"] ?? null, ${stored}["ado\\nazure_devops\\npersonal\\nOurs"] ?? null]`), [null, { sprint: "s-t1" }]);
    await closeWindow();
    await recover("Ours");
    await page.until(`${tabAsks}.at(-1).selections?.sprint === 's-t1'`, "the values chosen, under the new name");

    // Removed in the window.
    await openWindow();
    await page.clickText('.issue-views-window .issue-views-actions button', "Remove");
    await page.until(`!!document.querySelector('.provider-settings-confirm button')`, "the confirmation");
    await failedAfter("a view removed", async () => {
      await page.click('.provider-settings-confirm button');
      await page.until(`!issuesFixture.state.views.some(view => view.name === "Ours")`, "the view removed");
    });
    assert.equal(await page.evaluate(`${viewOptions}.includes("Ours")`), false, "the view removed is not shown");
    await closeWindow();
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the window of the views says why a file of views cannot be read, also when no view is left", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    const problem = ".alta/issues.toml does not parse: line 3, a key without a value.";
    const callouts = `[...document.querySelectorAll('.issue-views-window .bp6-callout')].map(item => item.textContent)`;
    // The file of the team does not parse, and the user has no view: nothing is left to read.
    await page.evaluate(`issuesFixture.state.views = []; issuesFixture.state.current = null; issuesFixture.state.viewsProblem = ${JSON.stringify(problem)}`);
    await page.until(`${rows}.join() === '#128'`, "the issues of the repository");
    await page.evaluate(choose('.issues-toolbar select[aria-label="Project"]', "ado"));
    await page.until(`[...document.querySelectorAll('.issues-notice')].some(item => item.textContent === ${JSON.stringify(problem)})`, "the problem, in the tab");
    await page.click('.issues-views-manage');
    await page.until(`${callouts}.length >= 2`, "the window of the views, with its problem");
    assert.deepEqual(await page.evaluate(callouts), [problem, "The views could not be read: the Issues tab lists every item."], "an unreadable file is not an empty one");
    await page.click('.issue-views-window [aria-label="Close"]');
    await page.until(`!document.querySelector('.issue-views-window')`, "the window closed");

    // The user's file still has a view: the window shows it, and says once why the team's are missing.
    await page.evaluate(`issuesFixture.state.views = [{ name: "Bugs", source: "personal", path: ".alta/local/issues.toml", filters: [{ id: "type", mode: "on", value: "Bug", label: null, pick: null }] }];
      issuesFixture.state.current = { name: "Bugs", source: "personal" }`);
    await page.click('[aria-label="Reload"]');
    await page.until(`document.querySelector('.issues-views select')?.selectedOptions[0]?.textContent === "Bugs"`, "the view of the user");
    await page.click('.issues-views-manage');
    await page.until(`!!document.querySelector('.issue-views-window .issue-views-table')`, "the window of the views, on the view of the user");
    assert.deepEqual(await page.evaluate(callouts), [problem]);
    assert.deepEqual(page.problems, []);
  }, options);
});
