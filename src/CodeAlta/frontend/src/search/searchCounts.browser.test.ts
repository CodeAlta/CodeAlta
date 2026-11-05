import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

// The categories of the search window in headless Edge: one that the host was not asked about says so, not 0.

const options = { entry: "../search/searchCounts.mount.tsx", fixture: "searchCountsFixture", rpcFixture: "../demo-api.ts", width: 1100, height: 800 } as const;
const tab = (name: string) => `[...document.querySelectorAll('.global-search-categories button')].find(button => button.textContent.startsWith('${name}'))`;
const count = (name: string) => `${tab(name)}?.querySelector('.global-search-count')?.textContent`;

test("a category not searched yet says ? until it is chosen, and keeps its number after", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until("document.activeElement?.id === 'search-input'", "the search opened");
    assert.equal(await page.evaluate(count("Sessions")), undefined, "Nothing typed, no counts.");

    // Ctrl+F opens on Sessions: what the page ranks itself is known, what the host finds was not asked for.
    await page.type("review");
    await page.until(`${count("Sessions")} === '1'`, "the sessions found");
    assert.equal(await page.evaluate(count("Projects")), "0", "A category searched that found nothing says 0.");
    for (const name of ["Conversations", "Files", "Docs", "Tools"]) {
      assert.equal(await page.evaluate(count(name)), "?", name);
      assert.equal(await page.evaluate(`${tab(name)}.dataset.empty`), undefined, `${name} is not shown as empty.`);
    }
    assert.equal(await page.evaluate(`${tab("Projects")}.dataset.empty`), "true");
    assert.deepEqual(await page.evaluate("searchCountsFixture.state.asked"), [], "The index was not asked.");

    // Choosing a category searches it; going back keeps what it found.
    await page.evaluate(`${tab("Conversations")}.click()`);
    await page.until(`${count("Conversations")} === '2'`, "the conversations found");
    assert.equal(await page.evaluate(count("Docs")), "?");
    await page.evaluate(`${tab("Sessions")}.click()`);
    await page.until(`document.querySelector('.global-search-row[data-kind="session"]')`, "the sessions shown");
    assert.equal(await page.evaluate(count("Conversations")), "2");

    // All searches everything.
    await page.evaluate(`${tab("All")}.click()`);
    await page.until(`${count("Docs")} === '1' && ${count("Files")} === '1' && ${count("Tools")} === '0'`, "every category answered");

    // Another text is not known yet, while the host works too.
    await page.evaluate("searchCountsFixture.hold()");
    await page.type("s");
    await page.until(`${count("Conversations")} === '?' && ${count("Docs")} === '?'`, "what was found for the other text is not shown as found for this one");
    await page.evaluate("searchCountsFixture.release()");
    await page.until(`${count("Conversations")} === '2'`, "the answer for this text");
  }, options);
});
