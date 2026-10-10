import assert from "node:assert/strict";
import test from "node:test";
import { createLandingPreferences, defaultLandingPreferences, landingAnimationKey, landingStartupKey } from "./landingPreferences";

function memory(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial));
  const written: [string, string][] = [];
  return { values, written, getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { written.push([key, value]); values.set(key, value); } };
}

test("a new profile opens the page at startup and animates it, under the keys the window keeps", () => {
  assert.deepEqual(defaultLandingPreferences, { openAtStartup: true, animate: true });
  assert.equal(landingStartupKey, "codealta.desktop.landing.startup.v1");
  assert.equal(landingAnimationKey, "codealta.desktop.landing.animation.v1");
  assert.deepEqual(createLandingPreferences(memory()).get(), { openAtStartup: true, animate: true });
  assert.deepEqual(createLandingPreferences(null).get(), { openAtStartup: true, animate: true });
});

test("what was kept is read: on, off, and the default for anything else", () => {
  assert.deepEqual(createLandingPreferences(memory({ [landingStartupKey]: "off", [landingAnimationKey]: "on" })).get(), { openAtStartup: false, animate: true });
  assert.deepEqual(createLandingPreferences(memory({ [landingStartupKey]: "on", [landingAnimationKey]: "off" })).get(), { openAtStartup: true, animate: false });
  for (const garbage of ["", "OFF", "false", "0", "no", " off"]) {
    assert.deepEqual(createLandingPreferences(memory({ [landingStartupKey]: garbage, [landingAnimationKey]: garbage })).get(), { openAtStartup: true, animate: true }, JSON.stringify(garbage));
  }
});

test("a choice is kept and told once to whoever listens; a choice that changes nothing is not", () => {
  const storage = memory();
  const store = createLandingPreferences(storage);
  let told = 0, other = 0;
  const stop = store.subscribe(() => { told++; });
  store.subscribe(() => { other++; });
  const before = store.get();

  store.set("openAtStartup", false);
  assert.deepEqual(store.get(), { openAtStartup: false, animate: true });
  assert.notEqual(store.get(), before, "a new value, so that what draws it draws again");
  assert.deepEqual(storage.written, [[landingStartupKey, "off"]]);
  assert.deepEqual([told, other], [1, 1]);

  const same = store.get();
  store.set("openAtStartup", false);
  store.set("animate", true);
  assert.equal(store.get(), same);
  assert.deepEqual(storage.written, [[landingStartupKey, "off"]], "nothing is written again");
  assert.deepEqual([told, other], [1, 1]);

  store.set("animate", false);
  store.set("openAtStartup", true);
  assert.deepEqual(storage.written, [[landingStartupKey, "off"], [landingAnimationKey, "off"], [landingStartupKey, "on"]]);
  assert.deepEqual([told, other], [3, 3]);

  stop();
  store.set("animate", true);
  assert.deepEqual([told, other], [3, 4], "one that stopped listening is told nothing more");
  // The next start of the window reads what was kept.
  assert.deepEqual(createLandingPreferences(storage).get(), { openAtStartup: true, animate: true });
  store.set("openAtStartup", false);
  assert.deepEqual(createLandingPreferences(storage).get(), { openAtStartup: false, animate: true });
});

test("a storage that cannot be read gives the defaults, and one that cannot be written keeps the choice for this run", () => {
  const unreadable = createLandingPreferences({ getItem: () => { throw new Error("denied"); }, setItem: () => { throw new Error("denied"); } });
  assert.deepEqual(unreadable.get(), { openAtStartup: true, animate: true });
  let told = 0;
  unreadable.subscribe(() => { told++; });
  unreadable.set("animate", false);
  unreadable.set("openAtStartup", false);
  assert.deepEqual(unreadable.get(), { openAtStartup: false, animate: false });
  assert.equal(told, 2);

  const full = createLandingPreferences({ getItem: key => key === landingAnimationKey ? "off" : null, setItem: () => { throw new Error("quota"); } });
  assert.deepEqual(full.get(), { openAtStartup: true, animate: false });
  full.set("animate", true);
  assert.deepEqual(full.get(), { openAtStartup: true, animate: true });

  const none = createLandingPreferences(null);
  none.set("openAtStartup", false);
  assert.equal(none.get().openAtStartup, false);
});

test("two stores share nothing but the storage they were given", () => {
  const first = createLandingPreferences(memory()), second = createLandingPreferences(memory());
  let told = 0;
  second.subscribe(() => { told++; });
  first.set("animate", false);
  first.set("openAtStartup", false);
  assert.deepEqual(first.get(), { openAtStartup: false, animate: false });
  assert.deepEqual(second.get(), { openAtStartup: true, animate: true });
  assert.equal(told, 0);
});
