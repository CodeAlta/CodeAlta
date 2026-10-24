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

test("each account shows its spaces on its row and in its form, where a click changes them", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    const chip = (key: string) => `document.querySelector('[data-provider-key="${key}"] .space-scope')`;
    await page.until(`${chip("claude-tools")}?.textContent === 'Only: Work'`, "the account of Work");
    assert.equal(await page.evaluate(`${chip("claude-code")}.textContent`), "All spaces");
    // The form of the selected provider has the same choice.
    await page.until(`document.querySelector('.provider-settings-form .space-scope')?.closest('.bp6-form-group')?.querySelector('label')?.textContent.trim() === 'Spaces'`,
      "the field");
    await page.click('[data-provider-key="claude-code"] .space-scope-button');
    await page.until(`!!document.querySelector('.space-scope-menu')`, "the spaces");
    await page.clickText('.space-scope-menu .bp6-menu-item', 'Work');
    await page.until(`claudeFixture.assigned.length === 1`, "the change saved at once");
    assert.equal(await page.evaluate(`JSON.stringify(claudeFixture.assigned[0])`), JSON.stringify({ kind: "provider", key: "claude-code", spaces: ["work"] }));
    await page.until(`${chip("claude-code")}?.textContent === 'Only: Work'`, "the row says it");
    assert.equal(await page.evaluate(`document.querySelector('.provider-settings-form .space-scope')?.textContent`), "Only: Work");
    assert.equal(await page.evaluate(`document.querySelector('[data-provider-key="claude-code"]').getAttribute('aria-current')`), "true", "the click on the chip does not select another row");
    // Every space chosen one by one is "All spaces" again, so that a space created later gets it too.
    await page.clickText('.space-scope-menu .bp6-menu-item', 'Default');
    await page.until(`claudeFixture.assigned.length === 2`, "the second change saved");
    assert.equal(await page.evaluate(`JSON.stringify(claudeFixture.assigned[1].spaces)`), "[]");
    await page.until(`${chip("claude-code")}?.textContent === 'All spaces'`, "the row says it is shared again");
    // Reload reads the spaces of the providers again, after a hand edit of the configuration.
    await page.clickText('button', 'Reload');
    await page.until(`claudeFixture.refreshes >= 1`, "the spaces read again");
  }, options);
});

test("the question of a removal closes once answered and when another provider is selected", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    const question = `(document.querySelector('.provider-settings-confirm')?.textContent ?? '')`;
    await page.until(`document.querySelector('[aria-label="Remove provider"]')`, "the form");
    await page.click('[aria-label="Remove provider"]');
    await page.until(`${question}.includes('Remove Claude Code from the configuration?')`, "the question");
    // Another provider selected: the question was not about it.
    await page.click('[data-provider-key="claude-tools"]');
    await page.until(`${question} === ''`, "no question for the other provider");
    await page.click('[aria-label="Remove provider"]');
    await page.until(`${question}.includes('Remove')`, "the question again");
    await page.clickText('.provider-settings-confirm button', 'Remove provider');
    await page.until(`claudeFixture.state.deleted[0] === 'claude-tools' && !document.querySelector('[data-provider-key="claude-tools"]')`, "the removal");
    // The next provider is selected: the question is not asked of it.
    await page.until(`document.querySelector('[data-provider-key="claude-code"]')?.getAttribute('aria-current') === 'true'`, "the next provider");
    await page.until(`${question} === ''`, "the question closed once answered");
    assert.equal(await page.evaluate(`${question}.includes('Claude Code')`), false, "never asked of the next provider");
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
