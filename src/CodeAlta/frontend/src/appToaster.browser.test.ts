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

test("A toast without a key is a toast of its own", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`${f}.show({ message: "one", timeout: 0 }); ${f}.show({ message: "one", timeout: 0 })`);
    await page.until(`${f}.messages().length === 2`, "two toasts");
  }, options);
});
