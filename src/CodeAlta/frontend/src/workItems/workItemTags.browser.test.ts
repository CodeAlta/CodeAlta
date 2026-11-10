import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../workItems/workItemTags.mount.tsx", fixture: "tagsFixture", rpcFixture: "../demo-api.ts", width: 900, height: 700,
  styles: ["../workItems/workItems.css"] } as const;
const chips = `[...document.querySelectorAll('.work-task-tags > .work-tag')].map(tag => tag.firstChild.textContent)`;
const calls = `tagsFixture.calls.slice()`;

test("a task's tags are added from the project's, removed, or created, and the tag manager edits, deletes and forgets", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.work-task-tags > .work-tag').length === 1`, "the tag of the task");
    assert.deepEqual(await page.evaluate(chips), ["mobile"]);

    // Add tag offers the project's other tags, found by a part of their name or description; Enter takes the first.
    await page.clickText(".work-tag-add", "Add tag");
    await page.until(`!!document.querySelector('.work-tag-picker input')`, "the picker");
    await page.click(".work-tag-picker input");
    await page.type("fla");
    await page.until(`[...document.querySelectorAll('.work-tag-picker .bp6-menu-item')].map(item => item.textContent).join('|') === 'flaky-testsA test that fails at random.|New tag "fla"…'`,
      "the tag that matches, then the offer of a new one");
    await page.key("Enter", 13);
    await page.until(`tagsFixture.calls.length === 1`, "the tags of the task saved");
    assert.deepEqual(await page.evaluate(calls), ["task:mobile,flaky-tests"]);
    await page.until(`${chips}.join() === 'mobile,flaky-tests'`, "the new chip");
    // The picker stays open for the next tag, its search cleared.
    assert.equal(await page.evaluate(`new Promise(done => setTimeout(() => done(document.querySelector('.work-tag-picker input')?.value ?? null), 600))`), "");
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.work-tag-picker')`, "the picker closed by Escape");

    // A chip removes its tag.
    await page.click(`.work-task-tags > .work-tag .work-tag-remove`, 0);
    await page.until(`tagsFixture.calls.length === 2`, "the tag removed");
    assert.equal(await page.evaluate(`tagsFixture.calls[1]`), "task:flaky-tests");

    // A name the project does not have is a new tag, created with what it is for, then given to the task.
    await page.clickText(".work-tag-add", "Add tag");
    await page.until(`!!document.querySelector('.work-tag-picker input')`, "the picker again");
    await page.click(".work-tag-picker input");
    await page.type("Desktop");
    await page.until(`[...document.querySelectorAll('.work-tag-picker .bp6-menu-item')].some(item => item.textContent.includes('New tag "desktop"'))`, "the offer of a new tag");
    await page.clickText(".work-tag-picker .bp6-menu-item", "New tag \"desktop\"…");
    await page.until(`!!document.querySelector('#work-tag-description')`, "the dialog of the new tag");
    await page.click("#work-tag-description");
    await page.type("The desktop window");
    await page.clickText(".work-tag-dialog .bp6-button", "Create tag");
    await page.until(`tagsFixture.calls.length === 5`, "the tag created, its tasks looked for, then given");
    assert.deepEqual(await page.evaluate(`tagsFixture.calls.slice(2)`), ["set:desktop:The desktop window", "find:desktop", "task:flaky-tests,desktop"]);

    // The manager lists the tags with what they are for, edits a name and a description together, deletes after asking, and forgets a declined name.
    await page.click("#manage");
    await page.until(`document.querySelectorAll('.work-tag-list > li').length === 4`, "the tags of the project");
    assert.equal(await page.evaluate(`document.querySelector('.work-tag-list > li:nth-child(3) em').textContent`), "Only on tasks: describe it so that agents use it");
    await page.click(`button[aria-label="Edit the tag flaky-tests"]`);
    await page.until(`!!document.querySelector('.work-tag-edit input')`, "the editor");
    await page.evaluate(`document.querySelector('.work-tag-edit input').select()`);
    await page.type("flaky");
    await page.clickText(".work-tag-edit .bp6-button", "Save");
    await page.until(`tagsFixture.calls.length === 6`, "the rename");
    assert.equal(await page.evaluate(`tagsFixture.calls[5]`), "rename:flaky-tests:flaky", "a description that did not change is not saved again");
    await page.until(`!document.querySelector('.work-tag-edit')`, "the editor closed");

    await page.click(`button[aria-label="Delete the tag desktop"]`);
    await page.until(`!!document.querySelector('.bp6-alert')`, "the confirmation");
    assert.match(await page.evaluate<string>(`document.querySelector('.bp6-alert').textContent`), /Delete the tag desktop\? No open task has it\./);
    await page.clickText(".bp6-alert .bp6-button", "Delete");
    await page.until(`tagsFixture.calls.length === 7`, "the deletion");
    assert.equal(await page.evaluate(`tagsFixture.calls[6]`), "delete:desktop:");

    await page.clickText(".work-tag-declined .bp6-button", "Forget");
    await page.until(`tagsFixture.calls.length === 8`, "the declined name forgotten");
    assert.equal(await page.evaluate(`tagsFixture.calls[7]`), "forget:ci-noise:");
    await page.until(`!document.querySelector('.work-tag-declined')`, "no declined names left");
    await page.clickText(".work-tag-manager .bp6-button", "Close");
    await page.until(`!document.querySelector('.work-tag-manager')`, "the manager closed");

    // A tag an agent proposes waits above the list; its review creates it, with the description the user left, for the tasks kept.
    assert.equal(await page.evaluate(`document.querySelector('.work-tag-proposal-text').textContent`), "An agent proposes the tag parser, for 2 open tasks.");
    await page.clickText(".work-tag-proposal .bp6-button", "Review");
    await page.until(`document.querySelectorAll('.work-tag-review-tasks li').length === 2`, "the tasks of the proposal");
    assert.equal(await page.evaluate(`document.querySelector('.work-tag-review-tasks li:nth-child(2)').textContent`), "q9rhLex the numbersthe lexer feeds it");
    await page.click(".work-tag-review-tasks li:nth-child(2) input");
    await page.until(`[...document.querySelectorAll('.work-tag-review .bp6-button')].some(button => button.textContent === 'Tag one task')`, "one task kept");
    await page.evaluate(`document.querySelector('#work-tag-review-description').select()`);
    await page.click("#work-tag-review-description");
    await page.evaluate(`document.querySelector('#work-tag-review-description').select()`);
    await page.type("Parsing files");
    await page.clickText(".work-tag-review .bp6-button", "Tag one task");
    await page.until(`tagsFixture.calls.length === 9`, "the proposal applied");
    assert.equal(await page.evaluate(`tagsFixture.calls[8]`), "apply:parser:Parsing files:2026-11-10-fix-the-parser-p53b");
    await page.until(`!document.querySelector('.work-tag-proposal') && !document.querySelector('.work-tag-review')`, "the proposal gone");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the tag picker stays open and in place for several tags, saves them in turn, and a click outside only closes it", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.work-task-tags > .work-tag').length === 1`, "the tag of the task");
    await page.clickText(".work-tag-add", "Add tag");
    await page.until(`!!document.querySelector('.work-tag-picker input')`, "the picker");
    const place = `(() => { const box = document.querySelector('.work-tag-picker').getBoundingClientRect(); return Math.round(box.x) + ',' + Math.round(box.y); })()`;
    const ticked = `[...document.querySelectorAll('.work-tag-picker .bp6-menu-item .work-tag-option > .work-tag')].map(tag => tag.textContent + (tag.closest('li').getAttribute('aria-selected') === 'true' ? '+' : '')).join()`;
    // Where the picker is once it has opened, past its transition.
    const opened = await page.evaluate<string>(`new Promise(done => setTimeout(() => done(${place}), 600))`);
    assert.equal(opened.split(",")[0], String(await page.evaluate(`Math.round(document.querySelector('.work-task-tags').getBoundingClientRect().x)`)), "the picker is under the start of the field");
    assert.equal(await page.evaluate(ticked), "mobile+,flaky-tests,handmade", "the tags of the task are in the list, ticked");

    // Two tags clicked before the first save is back: both show at once, and the second save keeps the first tag.
    await page.evaluate(`tagsFixture.held = true`);
    await page.clickText(".work-tag-picker .bp6-menu-item .work-tag-option > .work-tag", "flaky-tests");
    await page.until(`${chips}.join() === 'mobile,flaky-tests'`, "the first tag shown before its save is back");
    await page.clickText(".work-tag-picker .bp6-menu-item .work-tag-option > .work-tag", "handmade");
    await page.until(`${chips}.join() === 'mobile,flaky-tests,handmade'`, "the second tag shown too");
    assert.deepEqual(await page.evaluate(calls), ["task:mobile,flaky-tests"], "the second save waits for the first");
    await page.evaluate(`tagsFixture.release()`);
    await page.until(`tagsFixture.calls.length === 2`, "the second save");
    assert.deepEqual(await page.evaluate(calls), ["task:mobile,flaky-tests", "task:mobile,flaky-tests,handmade"]);
    assert.ok(await page.evaluate(`!!document.querySelector('.work-tag-picker')`), "the picker is still open");
    // Neither the picker nor its rows moved: it hangs from the field, and the tags added stay in it, ticked.
    assert.equal(await page.evaluate(place), opened, "the picker did not move");
    assert.equal(await page.evaluate(ticked), "mobile+,flaky-tests+,handmade+");

    // A ticked tag clicked is removed, and the search stays for the next one.
    await page.click(".work-tag-picker input");
    await page.type("hand");
    await page.until(`${ticked} === 'handmade+'`, "the search");
    await page.clickText(".work-tag-picker .bp6-menu-item .work-tag-option > .work-tag", "handmade");
    await page.until(`tagsFixture.calls.length === 3`, "the tag removed");
    assert.equal(await page.evaluate(`tagsFixture.calls[2]`), "task:mobile,flaky-tests");
    await page.until(`${ticked} === 'handmade'`, "the tag unticked");
    assert.equal(await page.evaluate(`document.querySelector('.work-tag-picker input').value`), "hand");

    // A click outside closes the picker without reaching what is under it.
    const point = await page.evaluate<{ x: number; y: number }>(`(() => { const box = document.querySelector('#behind').getBoundingClientRect(); return { x: box.x + box.width / 2, y: box.y + box.height / 2 }; })()`);
    await page.command("Input.dispatchMouseEvent", { type: "mousePressed", x: point.x, y: point.y, button: "left", clickCount: 1 });
    await page.command("Input.dispatchMouseEvent", { type: "mouseReleased", x: point.x, y: point.y, button: "left", clickCount: 1 });
    await page.until(`!document.querySelector('.work-tag-picker')`, "the picker closed");
    assert.equal(await page.evaluate(`tagsFixture.behind`), 0, "the click did not reach the button behind the picker");
    assert.deepEqual(await page.evaluate(chips), ["mobile", "flaky-tests"]);
    assert.deepEqual(page.problems, []);
  }, options);
});
