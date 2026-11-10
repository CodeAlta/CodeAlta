import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { edge, withCanvas, type Page } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

const options = { entry: "../automations/automationsPanel.mount.tsx", fixture: "automationsFixture", rpcFixture: "../demo-api.ts", styles: ["../automations/automations.css"],
  width: 1600, height: 1000, reducedMotion: true } as const;
const calls = `[...window.automationsFixture.calls].map(call => JSON.parse(call))`;
// Where the pictures go when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";
async function picture(page: Page, name: string) {
  if (!shots) return;
  await mkdir(shots, { recursive: true });
  const shot = await page.command("Page.captureScreenshot", { format: "png" }) as { data: string };
  await writeFile(join(shots, `${name}.png`), Buffer.from(shot.data, "base64"));
}

test("a run that waits for the person is taken over from the list, a try lists its calls, and the page holds in both themes and narrow",
  { skip: !edge, timeout: testLimit(120_000) }, async () => {
    await withCanvas(async page => {
      await page.until(`document.querySelectorAll('.automation-runs > li').length === 3`, "the runs");
      const rows = await page.evaluate(`[...document.querySelectorAll('.automation-runs > li')].map(li => [li.dataset.tone, li.querySelector('.automation-run-text span').textContent,
        li.querySelector('.automation-run-waits span')?.textContent ?? null, li.querySelectorAll('.automation-run-calls li').length])`);
      assert.deepEqual(rows, [
        ["running", "Waits for you · incident.io · INC-42 Checkout is down", "The run waits for you: answer it in its session, or take it over.", 0],
        ["running", "Waits for you · incident.io · INC-43 Search is slow", "The run waits for you: open its session on its machine to answer it.", 0],
        ["ok", "Completed · Try", null, 2],
      ]);
      for (const [theme, dark] of [["dark", true], ["light", false]] as const) {
        await page.evaluate(`document.documentElement.classList.toggle('bp6-dark', ${dark}); document.documentElement.dataset.theme = '${theme}'`);
        for (const [size, width, height] of [["wide", 1600, 1000], ["narrow", 900, 650]] as const) {
          await page.resize(width, height);
          await page.evaluate(`window.automationsFixture.setWidth(${width})`);
          await page.until(`document.querySelector('.automation-run-waits')`, "the list");
          assert.equal(await page.evaluate(`[...document.querySelectorAll('.automation-runs > li, .automation-run-waits, .automation-run-calls')]
            .filter(element => element.scrollWidth > element.clientWidth + 1).length`), 0, `nothing overflows, ${theme} ${size}`);
          assert.equal(await page.evaluate(`[...document.querySelectorAll('.automation-run-waits button')].every(button => { const box = button.getBoundingClientRect();
            return box.width > 40 && box.right <= innerWidth; })`), true, `Take over stays whole, ${theme} ${size}`);
          await page.evaluate(`document.querySelector('.automations-side').scrollIntoView({ block: 'start' })`);
          await picture(page, `automations-runs-${theme}-${size}`);
        }
      }

      // Taken over: the session of this computer opens, and the run no longer waits.
      await page.clickText(".automation-run-waits .bp6-button", "Take over");
      await page.until(`document.querySelectorAll('.automation-run-waits').length === 1`, "taken over");
      assert.deepEqual(await page.evaluate(calls), [{ takeOver: "r-local" }, { open: "s1" }]);

      // The automation: what its runs do without a person, what its tools are held to, who approves, and its try.
      await page.resize(1600, 1000);
      await page.evaluate(`window.automationsFixture.setWidth(1600)`);
      await page.click(".automation-card button");
      await page.until(`document.querySelector('.automation-autonomy-fact')`, "the autonomy of the automation");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.automation-autonomy-fact > span')].map(span => span.textContent)`), [
        "It calls k8s_get_deployment, k8s_restart_pod by itself; it asks you for anything else.",
        "k8s_restart_pod pinned to 1.4.2 · bounds on namespace, replicas · checks k8s_get_deployment first",
        "Its approvals go to the person on call of its team.",
      ]);
      await picture(page, "automations-detail-autonomy");
      await page.clickText(".automation-autonomy-fact .bp6-button", "Try it (dry run)");
      await page.until(`window.automationsFixture.calls.length === 3`, "tried");
      assert.deepEqual((await page.evaluate(calls) as unknown[])[2], { tryRun: "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77", index: -1 });
      assert.equal(await page.evaluate(`document.querySelector('.automation-detail header').scrollWidth <= document.querySelector('.automation-detail header').clientWidth + 1`), true,
        "the header keeps Run now and Edit on one line");

      // An automation of the team another maintainer gave: opened in this person's editor, as its machine holds it.
      await page.until(`document.querySelector('[data-team-automation="nightly"]')`, "the automations of the teams");
      await picture(page, "automations-team");
      await page.clickText("[data-team-automation=\"nightly\"] .bp6-button", "Open in my editor");
      await page.until(`document.querySelector('.automation-editor')`, "the editor");
      assert.deepEqual((await page.evaluate(calls) as unknown[])[3], { openTeam: "nightly" });
      assert.equal(await page.evaluate(`document.querySelector('.automation-editor input')?.value`), "Nightly builds");
      assert.equal(await page.evaluate(`document.querySelector('[data-team-automation]')`), null, "taken here, it is no longer offered");
      assert.deepEqual(page.problems, []);
    }, options);
  });
