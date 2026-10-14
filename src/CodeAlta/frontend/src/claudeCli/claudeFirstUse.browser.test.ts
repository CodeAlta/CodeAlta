import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../claudeCli/claudeFirstUse.mount.tsx", fixture: "firstUseFixture", rpcFixture: "../demo-api.ts", width: 900, height: 650 } as const;
const card = `document.querySelector('.claude-cli-first-use')`;

test("a Claude Code provider without its CLI proposes the download in place, follows it, then gives the login command", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`${card}?.textContent.includes('Claude Code CLI 2.1.292 is not installed')`, "the card of the missing CLI");
    await page.clickText('.claude-cli-first-use button', 'Download 2.1.292 (108.5 MiB)');
    await page.until(`firstUseFixture.installs === 1`, "the download asked");
    await page.evaluate(`firstUseFixture.tell({ state: "downloading", receivedBytes: "56898310", totalBytes: "113796621", error: null })`);
    await page.until(`${card}.querySelector('[role="status"]')?.textContent.includes('54.3 MiB of 108.5 MiB')`, "the live progress");
    await page.clickText('.claude-cli-first-use button', 'Cancel');
    await page.until(`firstUseFixture.cancels === 1`, "the cancel asked");

    // The download ends: the provider is tested once, and a CLI that is signed out gets the command that signs in.
    await page.evaluate(`firstUseFixture.installed = true;
      firstUseFixture.tell({ state: "done", receivedBytes: "113796621", totalBytes: "113796621", error: null })`);
    await page.until(`${card}?.querySelector('code')`, "the login command");
    assert.deepEqual(await page.evaluate(`firstUseFixture.probes`), ["claude-missing"]);
    assert.equal(await page.evaluate(`${card}.querySelector('code').textContent`),
      `$env:CLAUDE_CONFIG_DIR="$HOME\\.claude-work"; & "$HOME\\.alta\\cache\\claude-code\\2.1.292\\win-x64\\claude.exe"`);
    assert.equal(await page.evaluate(`${card}.textContent.includes('is not installed')`), false);
  }, options);
});

test("the card says nothing for a provider that has a CLI or does not run Claude Code, and says why a download failed", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`${card}`, "the card of the missing CLI");
    await page.evaluate(`firstUseFixture.tell({ state: "failed", receivedBytes: "0", totalBytes: "113796621", error: "network" })`);
    await page.until(`${card}.querySelector('[role="alert"]')?.textContent.includes('check the network connection')`, "the reason");
    await page.evaluate(`firstUseFixture.select("claude-code")`);
    await page.until(`!${card}`, "no card for a provider with its CLI");
    await page.evaluate(`firstUseFixture.select("codex")`);
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(await page.evaluate(`!!${card}`), false, "no card for another type");
    assert.equal(await page.evaluate(`firstUseFixture.probes.length`), 0, "nothing is tested without a download");
  }, options);
});
