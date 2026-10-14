import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../claudeCli/claudeProvider.mount.tsx", fixture: "claudeFixture", rpcFixture: "../demo-api.ts", width: 1600, height: 1000 } as const;
const panel = `document.querySelector('.provider-claude-cli')`;

test("the Claude Code form offers the pinned CLI, shows its download and gives the login command of the account", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`${panel}?.textContent.includes('Your version 2.1.300')`, "the CLI of the user, newer than the pin");
    assert.match(await page.evaluate(`${panel}.textContent`) as string, /tested with Claude Code 2\.1\.292.*\.local\\bin\\claude\.exe is used/);
    assert.equal(await page.evaluate(`${panel}.querySelector('code').textContent`), `& "$HOME\\.local\\bin\\claude.exe"`);

    // The folder of the account goes into the command as it is typed.
    await page.click('#provider-config-dir');
    await page.type('~/.claude-business');
    await page.until(`${panel}.querySelector('code').textContent.startsWith('$env:CLAUDE_CONFIG_DIR="$HOME\\\\.claude-business"; ')`, "the folder in the command");

    // The download is proposed, and its progress shown until the version is the pinned one.
    const download = `[...document.querySelectorAll('.provider-claude-cli button')].findIndex(button => /^Download 2\\.1\\.292 \\(108\\.5 MiB\\)$/.test(button.textContent.trim()))`;
    await page.until(`${download} >= 0`, "the download with its size");
    await page.click('.provider-claude-cli button', await page.evaluate(download) as number);
    await page.until(`claudeFixture.state.installs === 1`, "the download asked");
    await page.evaluate(`claudeFixture.state.tell({ state: "downloading", receivedBytes: "56898310", totalBytes: "113796621", error: null })`);
    await page.until(`${panel}.querySelector('[role="status"]')?.textContent.includes('Downloading Claude Code 2.1.292')`, "the progress");
    assert.equal(await page.evaluate(`!!${panel}.querySelector('.bp6-progress-bar')`), true);
    await page.evaluate(`Object.assign(claudeFixture.status, { pinnedPath: "C:\\\\Users\\\\me\\\\.alta\\\\cache\\\\claude-code\\\\2.1.292\\\\win-x64\\\\claude.exe" });
      claudeFixture.state.tell({ state: "done", receivedBytes: "113796621", totalBytes: "113796621", error: null })`);
    await page.until(`${panel}.textContent.includes('Version 2.1.292')`, "the pinned version once downloaded");
    assert.match(await page.evaluate(`${panel}.querySelector('code').textContent`) as string, /& "\$HOME\\\.alta\\cache\\claude-code\\2\.1\.292\\win-x64\\claude\.exe"$/);
    assert.equal(await page.evaluate(`[...${panel}.querySelectorAll('button')].some(button => button.textContent.includes('Download'))`), false);

    // A provider whose command is not there says so.
    await page.click('[data-provider-key="claude-tools"]');
    await page.until(`${panel}?.textContent.includes('Not found') && ${panel}.textContent.includes('it is not there')`, "the missing command");
  }, options);
});

test("changing the folder of a provider that has sessions asks first, and Duplicate adds another account", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#provider-config-dir')`, "the form");
    await page.click('#provider-config-dir');
    await page.type('~/.claude-work');
    await page.clickText('.provider-settings-footer button', 'Save and apply');
    await page.until(`document.querySelector('.provider-settings-confirm')?.textContent.includes('2 sessions use this provider')`, "the question beside the button");
    assert.equal(await page.evaluate(`claudeFixture.state.saved.length`), 0, "nothing is saved before the answer");
    await page.clickText('.provider-settings-confirm button', 'Change the folder');
    await page.until(`claudeFixture.state.saved.length === 1`, "the save");
    assert.equal(await page.evaluate(`claudeFixture.state.saved[0].provider.configDir`), "~/.claude-work");

    // A relative folder is refused before anything is sent.
    await page.click('#provider-config-dir');
    await page.evaluate(`document.querySelector('#provider-config-dir').select()`);
    await page.type('claude-work');
    await page.until(`document.querySelector('.provider-settings-problem')?.textContent.includes('full path')`, "the problem");

    await page.clickText('.provider-settings-footer button', 'Revert');
    await page.clickText('.provider-settings-footer button', 'Duplicate');
    await page.until(`claudeFixture.state.duplicated.length === 1`, "the copy");
    assert.equal(await page.evaluate(`claudeFixture.state.duplicated[0]`), "claude-code");
  }, options);
});
