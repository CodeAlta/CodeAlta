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

    // A project with one Jira project has nothing to choose: the tab names the project, as before.
    await page.evaluate(choose('.issues-toolbar select[aria-label="Project"]', "single"));
    await page.until(`${rows}.join() === 'DOCS-1'`, "the issues of the other project");
    assert.equal(await page.evaluate(`!!${part}`), false);
    assert.equal(await page.evaluate(`document.querySelector('.issues-source').textContent`), "JiraDOCS");
    assert.deepEqual(await page.evaluate(`[issuesFixture.state.lists.at(-1).projectId, issuesFixture.state.lists.at(-1).part]`), ["single", null]);
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
  }, options);
});
