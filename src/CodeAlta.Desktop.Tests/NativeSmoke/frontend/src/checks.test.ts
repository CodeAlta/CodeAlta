import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { createMockRpcHarness } from "@neoastra/client/testing";
import { check, eventually } from "./checks.js";

test("native smoke catch resets success when asynchronous disposal fails", () => {
  const source = readFileSync(new URL("../../ProbeApplication.cs", import.meta.url), "utf8");
  // Source guard for the test host's exit contract; not a claim of injected native disposal coverage.
  assert.match(source, /catch \(Exception exception\)\s*\{\s*ExitCode = 1;/);
});

test("failed checks and bounded waits fail explicitly", async () => {
  assert.throws(() => check(false, "expected"), /expected/);
  await assert.rejects(eventually(() => false, "deadline", 1), /Timed out: deadline/);
  let calls = 0;
  await eventually(() => ++calls === 2, "success");
  assert.equal(calls, 2);
});

test("package-staged mock transport agrees with the fake greeting shape", async () => {
  const harness = createMockRpcHarness();
  try {
    harness.register("probe.hello", ({ args }) => ({ message: `Hello, ${(args as { name: string }).name} (C#)` }));
    const result = await harness.client.invoke<{ name: string }, { message: string }>("probe.hello", { name: "desktop" });
    assert.equal(result.message, "Hello, desktop (C#)");
  } finally {
    harness.close();
  }
});
