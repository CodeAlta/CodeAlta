import assert from "node:assert/strict";
import test from "node:test";
import { createAppearancePreview, type AppearancePreview } from "./appearancePreview";

test("a preview reaches those who listen, once for each change", () => {
  const store = createAppearancePreview();
  const preview: AppearancePreview = { scheme: { id: "", name: "Draft", base: "blueprint", light: {}, dark: {}, darker: {} }, variant: "dark" };
  let calls = 0;
  const stop = store.subscribe(() => { calls++; });
  assert.equal(store.get(), null);
  store.set(preview);
  assert.equal(store.get(), preview);
  assert.equal(calls, 1);
  // The same preview again is no change.
  store.set(preview);
  assert.equal(calls, 1);
  store.set({ ...preview, variant: "light" });
  store.set(null);
  assert.equal(calls, 3);
  assert.equal(store.get(), null);
  stop();
  store.set(preview);
  assert.equal(calls, 3);
});

test("a listener that stops listening while it is called does not keep the others from hearing", () => {
  const store = createAppearancePreview();
  const heard: string[] = [];
  const stopFirst = store.subscribe(() => { heard.push("first"); stopFirst(); });
  store.subscribe(() => heard.push("second"));
  store.set({ scheme: { id: "", name: "Draft", base: "blueprint", light: {}, dark: {}, darker: {} }, variant: "darker" });
  store.set(null);
  assert.deepEqual(heard, ["first", "second", "second"]);
});
