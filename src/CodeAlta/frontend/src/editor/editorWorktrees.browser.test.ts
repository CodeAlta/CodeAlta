import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

// The worktree button of the header of the files of a project, in headless Edge under React StrictMode, with the real
// pointer: it shows only where the repository has more than one checkout, lists them with their branch, opens another
// worktree and leaves this editor as it is.

const options = { entry: "../editor/editorWorktrees.mount.tsx", fixture: "worktreesFixture", rpcFixture: "../demo-api.ts", width: 800, height: 500,
  styles: ["../editor/editor.css", "../worktrees/worktrees.css"] } as const;
const button = "button[aria-label=\"Open a worktree in the editor\"]";
const items = "[...document.querySelectorAll('.branch-switcher-list .bp6-menu-item')]";
// The popover takes the focus a moment after its rows show, and only then hears Escape.
const escape = async (page: { until(expression: string, what: string): Promise<void>; key(name: string, code: number): Promise<void> }) => {
  await page.until("document.activeElement?.closest('.bp6-overlay')", "the focus in the list");
  await page.key("Escape", 27);
};

test("the worktree button shows for a repository with several checkouts, and opens the one that is chosen", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    // A tab that is not shown reads nothing.
    await page.evaluate("worktreesFixture.show(false)");
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(await page.evaluate("worktreesFixture.state.lists"), 0);
    assert.equal(await page.evaluate(`!!document.querySelector('${button}')`), false);

    // A repository with a single checkout has nothing to choose from.
    await page.evaluate("worktreesFixture.state.worktrees = [worktreesFixture.project]; worktreesFixture.show(true)");
    await page.until("worktreesFixture.state.lists > 0", "the list read");
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(await page.evaluate(`!!document.querySelector('${button}')`), false);

    // With three checkouts the button is there, and lists each one by its name and its branch.
    await page.evaluate("worktreesFixture.clear(); worktreesFixture.state.worktrees = [worktreesFixture.project, worktreesFixture.heron, worktreesFixture.gone]; worktreesFixture.show(true)");
    await page.until(`document.querySelector('${button}')`, "the button");
    await page.click(button);
    await page.until(`${items}.length === 3`, "the three worktrees");
    assert.deepEqual(await page.evaluate(`${items}.map(item => item.querySelector('.bp6-fill')?.textContent)`), ["App", "quiet-heron", "old-fox"]);
    assert.deepEqual(await page.evaluate(`${items}.map(item => item.querySelector('.bp6-menu-item-label')?.textContent ?? '')`), ["main", "alta/quiet-heron", "Folder gone"]);
    // The editor of this tab is the one of the project: it is marked, and a folder that is gone cannot be chosen.
    assert.deepEqual(await page.evaluate("[...document.querySelectorAll('.branch-switcher-list li')].map(row => [row.getAttribute('aria-selected'), row.querySelector('a').classList.contains('bp6-disabled')])"),
      [["true", false], ["false", false], ["false", true]]);
    await page.shot("editor-worktrees-list");

    // The editor of the project is this one: choosing it opens nothing.
    await page.click(".branch-switcher-list .bp6-menu-item", 0);
    await page.until(`${items}.length === 0`, "the list closed");
    assert.deepEqual(await page.evaluate("worktreesFixture.state.opened"), []);

    // Another worktree is asked for by its folder, and this tab stays as it is.
    await page.click(button);
    await page.until(`${items}.length === 3`, "the three worktrees");
    await page.click(".branch-switcher-list .bp6-menu-item", 1);
    await page.until(`${items}.length === 0`, "the list closed");
    assert.deepEqual(await page.evaluate("worktreesFixture.state.opened"), ["/trees/app/quiet-heron"]);

    // A list that cannot be read keeps what is known: the button stays.
    await page.evaluate("worktreesFixture.state.failing = true");
    const lists = await page.evaluate<number>("worktreesFixture.state.lists");
    await page.click(button);
    await page.until(`worktreesFixture.state.lists > ${lists}`, "the failing read");
    await new Promise(resolve => setTimeout(resolve, 300));
    await page.until(`${items}.length === 3`, "the known worktrees while the host is gone");
    await escape(page);
    await page.until(`${items}.length === 0`, "the list closed");
    assert.equal(await page.evaluate(`!!document.querySelector('${button}')`), true);

    // A worktree that was removed meanwhile goes from the list when it is opened again.
    await page.evaluate("worktreesFixture.state.failing = false; worktreesFixture.state.worktrees = [worktreesFixture.project, worktreesFixture.gone]");
    await page.click(button);
    await page.until(`${items}.length === 2`, "the list read again");
    assert.deepEqual(await page.evaluate(`${items}.map(item => item.querySelector('.bp6-fill')?.textContent)`), ["App", "old-fox"]);
    await escape(page);
    await page.until(`${items}.length === 0`, "the list closed");

    // The last other checkout goes while the list is open: the button goes, and does not come back open.
    await page.evaluate("worktreesFixture.state.worktrees = [worktreesFixture.project, worktreesFixture.heron]");
    await page.click(button);
    await page.until(`${items}.length === 2`, "the list read again");
    await page.evaluate("worktreesFixture.state.worktrees = [worktreesFixture.project]; worktreesFixture.show(false); worktreesFixture.show(true)");
    await page.until(`!document.querySelector('${button}')`, "the button gone with the last checkout");
    await page.evaluate("worktreesFixture.state.worktrees = [worktreesFixture.project, worktreesFixture.heron]; worktreesFixture.show(false); worktreesFixture.show(true)");
    await page.until(`document.querySelector('${button}')`, "the button back");
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(await page.evaluate(`${items}.length`), 0, "the list does not open by itself");
    assert.equal(await page.evaluate(`document.querySelector('${button}').getAttribute('aria-expanded')`), "false");

    // A repository that is no more one has nothing to choose from: what was known goes.
    await page.evaluate("worktreesFixture.state.status = 'not_repository'; worktreesFixture.show(false); worktreesFixture.show(true)");
    await page.until(`!document.querySelector('${button}')`, "the button gone with the repository");
  }, options);
});
