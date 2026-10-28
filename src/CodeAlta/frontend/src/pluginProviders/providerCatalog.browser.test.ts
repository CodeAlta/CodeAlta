import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../pluginProviders/providerCatalog.mount.tsx", fixture: "catalogFixture", rpcFixture: "../demo-api.ts", width: 1100, height: 800 } as const;

// The window of the app opens no page of the web itself: the link of an agent goes to the host, which opens its browser.
test("the link of an agent of the catalog is opened by the host, not by the window", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`!!document.querySelector('.provider-catalog-link')`, "the card of the agent with its link");
    const before = await page.evaluate<string>("location.href");
    await page.evaluate(`document.querySelector('.provider-catalog-link').click()`);
    await page.until(`window.catalogFixture.opened.length === 1`, "the link handed to the host");
    assert.deepEqual(await page.evaluate<string[]>("window.catalogFixture.opened"), ["https://cursor.com/cli"]);
    assert.equal(await page.evaluate<string>("location.href"), before, "the page did not follow the link");
  }, options);
});
