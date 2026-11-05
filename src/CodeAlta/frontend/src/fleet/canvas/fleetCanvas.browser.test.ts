import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../../statistics/browserHarness";

const options = { entry: "../fleet/canvas/fleetCanvas.mount.tsx", fixture: "fleetCanvasFixture", rpcFixture: "../demo-api.ts", styles: ["../fleet/fleet.css"], width: 1100,
  height: 700, reducedMotion: true } as const;
const calls = async (page: { evaluate: (script: string) => Promise<unknown> }) => (await page.evaluate(`[...window.fleetCanvasFixture.calls]`) as string[]).map(call => JSON.parse(call));

test("without a server, the Fleet canvas shows the paired machines and opens the page of one from its name", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-canvas[data-directory=paired] .fleet-machine')`, "the paired machines");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-canvas-title h1').textContent`), "Paired machines");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-machine')].map(card => card.getAttribute('aria-label') + ': ' + card.querySelector('.fleet-machine-count').textContent)`),
      ["Mac mini: 2 sessions · 1 at work", "build box: 0 sessions"], "online first, with its sessions at work");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-machine[aria-label="Mac mini"] .fleet-machine-details').textContent`), "macos · CodeAlta 4.0.1 · Codex");
    // The name fits its card: the button keeps the line of the title.
    assert.equal(await page.evaluate(`(() => { const name = document.querySelector('.fleet-machine-open'); const title = name.closest('.fleet-machine-title');
      return name.getBoundingClientRect().height <= title.getBoundingClientRect().height && name.scrollWidth <= name.clientWidth; })()`), true);
    // Open keeps one line, with room to spare for a wider system font or a longer word (on macOS it wrapped as "Op / en").
    const room = await page.evaluate<{ cell: number; natural: number; height: number }>(`(() => { const open = document.querySelector('.fleet-session-open .bp6-button');
      const cell = open.closest('td'); const style = getComputedStyle(cell); open.style.whiteSpace = 'nowrap'; const natural = open.getBoundingClientRect().width; open.style.whiteSpace = '';
      return { cell: cell.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight), natural, height: open.getBoundingClientRect().height }; })()`);
    assert.ok(room.cell - room.natural >= 16 && room.height <= 30, JSON.stringify(room));
    await page.shot("fleet-canvas-paired");

    await page.clickText(".fleet-machine-open", "Mac mini");
    await page.until(`window.fleetCanvasFixture.calls.length > 0`, "the page asked");
    assert.deepEqual(await calls(page), [{ machine: "mini-id" }]);
    await page.clickText(".fleet-session-open .bp6-button", "Open");
    assert.deepEqual((await calls(page)).at(-1), { session: "s-works" }, "a session of a paired machine opens in a tab");
    // Settings is one click away: the page of the machines, while no server is linked.
    await page.clickText(".fleet-canvas-settings", "Settings");
    assert.deepEqual((await calls(page)).at(-1), { settings: "machines" });
  }, options);
});
