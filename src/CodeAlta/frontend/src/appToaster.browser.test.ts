import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";

const options = { entry: "../appToaster.mount.tsx", fixture: "toasterFixture" };
const f = "window.toasterFixture";
const pause = (milliseconds: number) => new Promise(resolve => setTimeout(resolve, milliseconds));

test("A keyed toast said again before the first one is on screen replaces it", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    // A session cleanup reports "running" and, a few microtasks later, its outcome: both reach the toaster before it has
    // rendered the first. The second must replace the first, not stand beside it: a "running" notice never expires.
    await page.evaluate(`${f}.show({ message: "running", timeout: 0 }, "job"); ${f}.show({ message: "running 55 of 55", timeout: 0 }, "job"); ${f}.show({ message: "done", timeout: 0 }, "job")`);
    await page.until(`${f}.messages().includes("done")`, "the last message");
    await pause(600);
    assert.deepEqual(await page.evaluate<string[]>(`${f}.messages()`), ["done"]);
  }, options);
});

test("A keyed toast replaced by its outcome expires with it", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`${f}.show({ message: "running", timeout: 0 }, "job"); ${f}.show({ message: "done", timeout: 300 }, "job")`);
    await page.until(`${f}.messages().length === 0`, "the outcome to expire and nothing else of the key to stay");
  }, options);
});

test("Keyed toasts replace in place, stay apart by key, and a dismissed key shows again", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`${f}.show({ message: "a 1", timeout: 0 }, "a"); ${f}.show({ message: "b 1", timeout: 0 }, "b")`);
    await page.until(`${f}.messages().length === 2`, "both toasts");
    // The toast is in the toaster now: the next message takes its place without waiting for anything.
    await page.evaluate(`${f}.show({ message: "a 2", timeout: 0 }, "a")`);
    await page.until(`${f}.messages().includes("a 2")`, "the message of a to be replaced");
    assert.deepEqual([...await page.evaluate<string[]>(`${f}.messages()`)].sort(), ["a 2", "b 1"]);

    await page.evaluate(`${f}.dismiss("a")`);
    await page.until(`!${f}.messages().includes("a 2") && ${f}.messages().includes("b 1")`, "a to go and b to stay");
    await page.evaluate(`${f}.show({ message: "a 3", timeout: 0 }, "a"); ${f}.show({ message: "a 4", timeout: 0 }, "a")`);
    await page.until(`${f}.messages().includes("a 4")`, "the key to show again");
    await pause(300);
    assert.deepEqual([...await page.evaluate<string[]>(`${f}.messages()`)].sort(), ["a 4", "b 1"]);
  }, options);
});

test("A toast is above a window of the app and can be clicked there, and goes back to the page once it closes", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    // A window of the app is a modal <dialog>: above any z-index, and the rest of the page is inert under it.
    await page.evaluate(`(() => { const d = document.createElement("dialog"); d.id = "window"; d.style.cssText = "width: 100vw; height: 100vh; max-width: none; max-height: none; margin: 0; padding: 0; border: 0";
      document.body.append(d); d.showModal(); window.acted = 0; })()`);
    await page.evaluate(`${f}.show({ message: "pc-ai asks to join", timeout: 0, action: { text: "Approve", onClick: () => { window.acted++; } } }, "join")`);
    await page.until(`${f}.messages().includes("pc-ai asks to join")`, "the toast");
    const hit = `(() => { const button = [...document.querySelectorAll(".app-toaster .bp6-toast .bp6-button")].find(b => b.textContent === "Approve");
      const r = button.getBoundingClientRect(); const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2); return !!top && button.contains(top); })()`;
    await page.until(hit, "the button of the toast to be what a press there reaches, not the window");
    // At the top right of the window, as wide as a toast is.
    const box = await page.evaluate<{ left: number; right: number; width: number; viewport: number }>(`(() => { const r = document.querySelector(".app-toaster .bp6-toast").getBoundingClientRect();
      return { left: r.left, right: r.right, width: r.width, viewport: innerWidth }; })()`);
    assert.ok(box.width > 200 && box.right > box.viewport - 40 && box.left > box.viewport / 3, JSON.stringify(box));
    // The layer over the window lets a press beside the toasts reach the window.
    assert.equal(await page.evaluate<string>(`document.elementFromPoint(20, innerHeight - 20)?.id ?? ""`), "window");
    await page.evaluate(`document.elementFromPoint(...(() => { const r = document.querySelector(".app-toaster .bp6-toast .bp6-button").getBoundingClientRect(); return [r.left + 4, r.top + 4]; })()).click()`);
    assert.equal(await page.evaluate<number>("window.acted"), 1);

    await page.evaluate(`${f}.show({ message: "still here", timeout: 0 }, "kept")`);
    await page.evaluate(`document.getElementById("window").close()`);
    await page.until(`document.querySelector(".app-toaster-layer")?.parentElement === document.body && document.querySelector(".app-toaster-layer").matches(":popover-open")`,
      "the toasts to go back to the page with the window closed");
    assert.ok(await page.evaluate<boolean>(`${f}.messages().includes("still here")`));
  }, options);
});

test("A toast without a key is a toast of its own", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`${f}.show({ message: "one", timeout: 0 }); ${f}.show({ message: "one", timeout: 0 })`);
    await page.until(`${f}.messages().length === 2`, "two toasts");
  }, options);
});
