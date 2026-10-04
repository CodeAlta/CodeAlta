import assert from "node:assert/strict";
import test from "node:test";
import { canClearNotes, copyNotesMarkdown, createNotesReader, maximumNotesUnits, type NotesClearState, type NotesState } from "./sessionNotes";
import { createMutationCapability } from "./sessionOperations";

const epoch = "11111111-1111-4111-8111-111111111111";
const otherEpoch = "22222222-2222-4222-8222-222222222222";
const reply = (markdown = "") => ({ status: "ok", hostEpoch: epoch, sessionId: "session", markdown });

test("notes require explicit refresh and preserve empty and complete literal text", async () => {
  let calls = 0;
  let markdown = "";
  const owner = createNotesReader(async () => { calls++; return reply(markdown); });
  const controller = new AbortController();
  const states: NotesState[] = [];
  const selection = owner.forSelection(epoch, "session", controller.signal, value => states.push(value), () => {});
  const originals: Promise<unknown>[] = [];
  let primary: unknown;
  try {
    assert.equal(calls, 0);
    assert.equal(states.length, 0);
    acquire(originals, () => selection.refresh());
    await join(originals[0], owner);
    assert.deepEqual(states.at(-1), { kind: "ready", markdown: "" });
    markdown = "# Literal\r\n<script>not HTML</script>\n\u0085\ufeff😀  ";
    acquire(originals, () => selection.refresh());
    await join(originals[1], owner);
    assert.deepEqual(states.at(-1), { kind: "ready", markdown });
    assert.equal(calls, 2);
  } catch (error) { primary = error; throw error; }
  finally { controller.abort(); await finish(originals, owner, primary); }
});

test("notes exclude same-turn overlap and retain the original across selection and remount", async () => {
  let release!: (value: unknown) => void;
  const original = new Promise<unknown>(resolve => { release = resolve; });
  let calls = 0;
  const owner = createNotesReader(() => { calls++; return original; });
  const first = new AbortController(); const second = new AbortController();
  const before: NotesState[] = []; const after: NotesState[] = [];
  const a = owner.forSelection(epoch, "session", first.signal, value => before.push(value), () => {});
  const originals: Promise<unknown>[] = [original];
  const work = acquire(originals, () => a.refresh());
  let primary: unknown;
  try {
    assert.equal(a.refresh(), work);
    first.abort();
    const b = owner.forSelection(epoch, "other", second.signal, value => after.push(value), () => {});
    const competing = acquire(originals, () => b.refresh());
    assert.equal(competing, work);
    assert.equal(a.refresh(), work);
    release(reply("old"));
    await join(work, owner);
    assert.equal(calls, 1);
    assert.equal(before.some(value => value.kind === "ready"), false);
    assert.equal(after.some(value => value.kind === "ready"), false);
    assert.equal(after.at(-1)?.kind, "error");
    // Reusing the same session ID creates a new selection, not permission for an old callback to publish.
    const c = owner.forSelection(epoch, "session", second.signal, value => after.push(value), () => {});
    const next = acquire(originals, () => c.refresh());
    await join(next, owner);
    assert.equal(calls, 2);
    assert.deepEqual(after.at(-1), { kind: "ready", markdown: "old" });
  } catch (error) { primary = error; throw error; }
  finally { first.abort(); second.abort(); release(reply()); await finish(originals, owner, primary); }
});

test("late notes epoch evidence revokes shared capability before obsolete presentation", async () => {
  for (const staleStatus of [false, true]) {
    let release!: (value: unknown) => void;
    const original = new Promise<unknown>(resolve => { release = resolve; });
    let calls = 0;
    const owner = createNotesReader(() => { calls++; return original; });
    const capability = createMutationCapability(epoch);
    const revoke = () => { capability.observe({ status: "stale_epoch", epoch }); };
    const old = new AbortController(); const current = new AbortController();
    const states: NotesState[] = [];
    const a = owner.forSelection(epoch, "session", old.signal, value => states.push(value), revoke);
    const originals: Promise<unknown>[] = [original];
    const work = acquire(originals, () => a.refresh());
    let primary: unknown;
    try {
      old.abort();
      const next = owner.forSelection(epoch, "session", current.signal, value => states.push(value), revoke);
      release({ status: staleStatus ? "stale_epoch" : "ok", hostEpoch: staleStatus ? epoch : otherEpoch, sessionId: "session", markdown: staleStatus ? null : "old" });
      await join(work, owner);
      assert.equal(capability.canMutate(), false);
      assert.equal(states.some(value => value.kind === "ready"), false);
      const refused = acquire(originals, () => next.refresh());
      await join(refused, owner);
      assert.equal(calls, 1);
      assert.deepEqual(states.at(-1), { kind: "error", code: "stale_epoch" });
    } catch (error) { primary = error; throw error; }
    finally { old.abort(); current.abort(); release(reply()); await finish(originals, owner, primary); }
  }
});

test("notes validate complete responses and distinguish failures from empty notes", async () => {
  const invalid: unknown[] = [null, {}, reply("x".repeat(maximumNotesUnits + 1)), reply("\ud800"), reply("\udc00"),
    { ...reply(), sessionId: "other" }, { ...reply(), hostEpoch: epoch + "\n" },
    { ...reply(), markdown: null }, { ...reply(), status: "unknown" },
    { ...reply("private"), status: "read_failed" }, { ...reply(), extra: "ignored" }];
  for (const response of invalid) {
    const owner = createNotesReader(async () => response);
    const controller = new AbortController();
    const states: NotesState[] = [];
    const originals: Promise<unknown>[] = [];
    const work = acquire(originals, () => owner.forSelection(epoch, "session", controller.signal, value => states.push(value), () => {}).refresh());
    let primary: unknown;
    try {
      await join(work, owner);
      assert.equal(states.at(-1)?.kind, response === invalid.at(-1) ? "ready" : "error");
    } catch (error) { primary = error; throw error; }
    finally { controller.abort(); await finish(originals, owner, primary); }
  }
  for (const status of ["missing_session", "closed", "capacity", "read_failed", "wire_limit", "invalid_request"]) {
    const owner = createNotesReader(async () => ({ status, hostEpoch: epoch, sessionId: "session", markdown: null }));
    const controller = new AbortController();
    const states: NotesState[] = [];
    const originals: Promise<unknown>[] = [];
    const work = acquire(originals, () => owner.forSelection(epoch, "session", controller.signal, value => states.push(value), () => {}).refresh());
    let primary: unknown;
    try { await join(work, owner); assert.deepEqual(states.at(-1), { kind: "error", code: status }); }
    catch (error) { primary = error; throw error; }
    finally { controller.abort(); await finish(originals, owner, primary); }
  }
});

test("notes as long as the host sends are shown whole", async () => {
  const long = "# Plan\n" + "x".repeat(maximumNotesUnits - 7);
  const owner = createNotesReader(async () => reply(long));
  const controller = new AbortController();
  const states: NotesState[] = [];
  const originals: Promise<unknown>[] = [];
  const work = acquire(originals, () => owner.forSelection(epoch, "session", controller.signal, value => states.push(value), () => {}).refresh());
  let primary: unknown;
  try { await join(work, owner); assert.deepEqual(states.at(-1), { kind: "ready", markdown: long }); }
  catch (error) { primary = error; throw error; }
  finally { controller.abort(); await finish(originals, owner, primary); }
});

test("notes reject invalid captured identities without invoking transport", async () => {
  let calls = 0;
  const owner = createNotesReader(async () => { calls++; return reply(); });
  const originals: Promise<unknown>[] = [];
  const controller = new AbortController();
  let primary: unknown;
  try {
    for (const [host, session] of [[epoch + "\n", "session"], [epoch.toUpperCase().replace("11111111", "ABCDEFAB"), "session"],
      [epoch, ""], [epoch, " session"], [epoch, "session\u0085"], [epoch, "x".repeat(257)], [epoch, "\ud800"]]) {
      const states: NotesState[] = [];
      const work = acquire(originals, () => owner.forSelection(host, session, controller.signal, value => states.push(value), () => {}).refresh());
      await join(work, owner);
      assert.deepEqual(states.at(-1), { kind: "error", code: "invalid_request" });
    }
    assert.equal(calls, 0);
  } catch (error) { primary = error; throw error; }
  finally { controller.abort(); await finish(originals, owner, primary); }
});

test("failed notes reads allow only an explicit fresh read and accept the complete boundary", async () => {
  let calls = 0;
  const owner = createNotesReader(async () => { if (++calls === 1) throw new Error("private failure"); return reply("😀".repeat(8192)); });
  const controller = new AbortController(); const states: NotesState[] = [];
  const selection = owner.forSelection(epoch, "session", controller.signal, value => states.push(value), () => {});
  const originals: Promise<unknown>[] = [];
  let primary: unknown;
  try {
    acquire(originals, () => selection.refresh()); await join(originals[0], owner);
    assert.deepEqual(states.at(-1), { kind: "error", code: "read_failed" });
    assert.equal(calls, 1);
    acquire(originals, () => selection.refresh()); await join(originals[1], owner);
    assert.deepEqual(states.at(-1), { kind: "ready", markdown: "😀".repeat(8192) });
    assert.equal(calls, 2);
  } catch (error) { primary = error; throw error; }
  finally { controller.abort(); await finish(originals, owner, primary); }
});

test("clear captures the selected session, excludes overlap and does not publish into a switched session", async () => {
  let release!: (value: unknown) => void;
  const pending = new Promise<unknown>(resolve => { release = resolve; });
  const targets: string[] = [];
  const owner = createNotesReader(async request => reply(request.sessionId), request => { targets.push(request.sessionId); return pending; });
  const first = new AbortController(); const second = new AbortController();
  const before: NotesState[] = []; const after: NotesState[] = [];
  const actions: NotesClearState[] = [];
  const originals: Promise<unknown>[] = [pending];
  let primary: unknown;
  try {
    const a = owner.forSelection(epoch, "session", first.signal, state => before.push(state), () => {});
    await join(acquire(originals, () => a.refresh()), owner);
    let cleared = 0;
    const work = acquire(originals, () => a.clear(state => actions.push(state), () => cleared++));
    assert.equal(a.clear(state => actions.push(state), () => cleared++), work);
    first.abort();
    const b = owner.forSelection(epoch, "other", second.signal, state => after.push(state), () => {});
    assert.equal(b.refresh(), work);
    assert.equal(b.clear(state => actions.push(state), () => cleared++), work);
    release({ status: "ok", hostEpoch: epoch, sessionId: "session" });
    await join(work, owner);
    assert.deepEqual(targets, ["session"]);
    assert.equal(cleared, 0);
    assert.equal(after.some(value => value.kind === "ready"), false);
  } catch (error) { primary = error; throw error; }
  finally { first.abort(); second.abort(); release({ status: "ok", hostEpoch: epoch, sessionId: "session" }); await finish(originals, owner, primary); }
});

test("clear reports success only for matching valid acknowledgement, preserves read and failures otherwise", async () => {
  const responses: unknown[] = [null, {}, { status: "ok", hostEpoch: epoch, sessionId: "other" },
    { status: "clear_unconfirmed", hostEpoch: epoch, sessionId: "session" },
    { status: "ok", hostEpoch: epoch, sessionId: "session" }];
  for (const response of responses) {
    const owner = createNotesReader(async () => reply("# Keep"), async () => response);
    const controller = new AbortController();
    const states: NotesState[] = []; const actions: NotesClearState[] = [];
    const originals: Promise<unknown>[] = [];
    let cleared = 0; let primary: unknown;
    try {
      const selected = owner.forSelection(epoch, "session", controller.signal, state => states.push(state), () => {});
      await join(acquire(originals, () => selected.refresh()), owner);
      await join(acquire(originals, () => selected.clear(state => actions.push(state), () => cleared++)), owner);
      assert.equal(cleared, response === responses.at(-1) ? 1 : 0);
      assert.deepEqual(states.at(-1), { kind: "ready", markdown: cleared ? "" : "# Keep" });
      assert.deepEqual(actions.at(-1), cleared ? { kind: "cleared" } : { kind: "error", code: "clear_unconfirmed" });
    } catch (error) { primary = error; throw error; }
    finally { controller.abort(); await finish(originals, owner, primary); }
  }
});

test("copy failure does not alter Markdown or silently claim success", async () => {
  const content = "# Notes\r\nKeep exact Markdown";
  let copied = "";
  assert.equal(await copyNotesMarkdown(content, async text => { copied = text; }), "copied");
  assert.equal(copied, content);
  assert.equal(await copyNotesMarkdown(content, async () => { throw new Error("denied"); }), "copy_failed");
  assert.equal(copied, content);
});

test("clear response from another host revokes future mutations even after switching sessions", async () => {
  const owner = createNotesReader(async () => reply("# Current"), async () =>
    ({ status: "clear_unconfirmed", hostEpoch: otherEpoch, sessionId: "session" }));
  const first = new AbortController(); const second = new AbortController();
  const original: Promise<unknown>[] = [];
  let revoked = 0; let calls = 0; let primary: unknown;
  try {
    const a = owner.forSelection(epoch, "session", first.signal, () => {}, () => revoked++);
    await join(acquire(original, () => a.refresh()), owner);
    const clear = acquire(original, () => a.clear(() => {}, () => calls++));
    first.abort();
    owner.forSelection(epoch, "other", second.signal, () => {}, () => revoked++);
    await join(clear, owner);
    assert.equal(revoked, 1);
    assert.equal(calls, 0);
  } catch (error) { primary = error; throw error; }
  finally { first.abort(); second.abort(); await finish(original, owner, primary); }
});

test("uncertain clear refuses repeat across selection and remount until an explicit fresh read proves content", async () => {
  let release!: (value: unknown) => void;
  const pending = new Promise<unknown>(resolve => { release = resolve; });
  let calls = 0;
  const owner = createNotesReader(async request => ({ ...reply("# Keep"), sessionId: request.sessionId }),
    async request => ++calls === 1 ? pending : { status: "ok", hostEpoch: epoch, sessionId: request.sessionId });
  const first = new AbortController(); const second = new AbortController();
  const originals: Promise<unknown>[] = [pending];
  let primary: unknown;
  try {
    const old = owner.forSelection(epoch, "session", first.signal, () => {}, () => {});
    await join(acquire(originals, () => old.refresh()), owner);
    const work = acquire(originals, () => old.clear(() => {}, () => {}));
    first.abort();
    owner.forSelection(epoch, "other", second.signal, () => {}, () => {});
    release({ status: "clear_unconfirmed", hostEpoch: epoch, sessionId: "session" });
    await join(work, owner);
    const returned = owner.forSelection(epoch, "session", second.signal, () => {}, () => {});
    assert.deepEqual(returned.uncertainClear(), { kind: "error", code: "clear_unconfirmed" });
    assert.equal(canClearNotes({ kind: "ready", markdown: "# Keep" }, returned.uncertainClear(), true, undefined), false);
    await join(acquire(originals, () => returned.clear(() => {}, () => {})), owner);
    assert.equal(calls, 1);
    await join(acquire(originals, () => returned.refresh()), owner); // Automatic selection refresh does not authorize retry.
    await join(acquire(originals, () => returned.clear(() => {}, () => {})), owner);
    assert.equal(calls, 1);
    const retry = owner.forSelection(epoch, "session", second.signal, () => {}, () => {});
    await join(acquire(originals, () => retry.reconcile()), owner); // Explicit request, validated nonempty response.
    assert.equal(retry.uncertainClear(), undefined);
    assert.equal(canClearNotes({ kind: "ready", markdown: "# Keep" }, retry.uncertainClear(), true, undefined), true);
    await join(acquire(originals, () => retry.clear(() => {}, () => {})), owner);
    assert.equal(calls, 2);
    assert.equal(retry.uncertainClear(), undefined);
  } catch (error) { primary = error; throw error; }
  finally { first.abort(); second.abort(); release({ status: "clear_unconfirmed", hostEpoch: epoch, sessionId: "session" }); await finish(originals, owner, primary); }
});

test("uncertain clear stays locked after failed, malformed or empty explicit reads", async () => {
  const reads: unknown[] = [reply("# Initial"), { ...reply(), status: "read_failed", markdown: null },
    { ...reply(), sessionId: "other" }, reply(""), reply("# Verified")];
  let calls = 0; let clears = 0;
  const owner = createNotesReader(async () => reads[calls++], async () => { clears++; return { status: "clear_unconfirmed", hostEpoch: epoch, sessionId: "session" }; });
  const controller = new AbortController(); const originals: Promise<unknown>[] = [];
  let primary: unknown;
  try {
    const selected = owner.forSelection(epoch, "session", controller.signal, () => {}, () => {});
    await join(acquire(originals, () => selected.refresh()), owner);
    await join(acquire(originals, () => selected.clear(() => {}, () => {})), owner);
    for (let index = 1; index < reads.length - 1; index++) {
      await join(acquire(originals, () => selected.reconcile()), owner);
      assert.deepEqual(selected.uncertainClear(), { kind: "error", code: "clear_unconfirmed" });
      assert.equal(canClearNotes({ kind: "ready", markdown: "# Initial" }, selected.uncertainClear(), true, undefined), false);
      await join(acquire(originals, () => selected.clear(() => {}, () => {})), owner);
      assert.equal(clears, 1);
    }
    await join(acquire(originals, () => selected.reconcile()), owner);
    assert.equal(selected.uncertainClear(), undefined);
    await join(acquire(originals, () => selected.clear(() => {}, () => {})), owner);
    assert.equal(clears, 2);
  } catch (error) { primary = error; throw error; }
  finally { controller.abort(); await finish(originals, owner, primary); }
});

test("clear controls agree with owner lock, pending state and explicit reconciliation", () => {
  const ready: NotesState = { kind: "ready", markdown: "# Retained" };
  const uncertain: NotesClearState = { kind: "error", code: "clear_unconfirmed" };
  assert.equal(canClearNotes(ready, uncertain, true, uncertain), false);
  assert.equal(canClearNotes(ready, undefined, true, uncertain), false); // Until visible warning is cleared.
  assert.equal(canClearNotes(ready, undefined, true, undefined), true);
  assert.equal(canClearNotes(ready, undefined, false, undefined), false);
  assert.equal(canClearNotes(ready, undefined, true, { kind: "clearing" }), false);
  assert.equal(canClearNotes({ kind: "error", code: "read_failed" }, undefined, true, undefined), false);
  assert.equal(canClearNotes({ kind: "ready", markdown: "" }, undefined, true, undefined), false);
});

test("host change drops old uncertainty without admitting stale callbacks on the new epoch", async () => {
  let release!: (value: unknown) => void;
  const pending = new Promise<unknown>(resolve => { release = resolve; });
  let calls = 0; const owner = createNotesReader(async request => ({ ...reply("# Existing"), sessionId: request.sessionId }),
    request => { calls++; return request.sessionId === "session" ? Promise.resolve({ status: "clear_unconfirmed", hostEpoch: epoch, sessionId: "session" }) : pending; });
  const first = new AbortController(); const second = new AbortController(); const originals: Promise<unknown>[] = [pending];
  let primary: unknown;
  try {
    const before = owner.forSelection(epoch, "session", first.signal, () => {}, () => {});
    await join(acquire(originals, () => before.refresh()), owner);
    await join(acquire(originals, () => before.clear(() => {}, () => {})), owner);
    assert.deepEqual(before.uncertainClear(), { kind: "error", code: "clear_unconfirmed" });
    const other = owner.forSelection(epoch, "other", first.signal, () => {}, () => {});
    await join(acquire(originals, () => other.refresh()), owner);
    const original = acquire(originals, () => other.clear(() => {}, () => {}));
    first.abort();
    const after = owner.forSelection(otherEpoch, "session", second.signal, () => {}, () => {});
    assert.equal(after.uncertainClear(), undefined);
    assert.equal(before.uncertainClear(), undefined);
    release({ status: "clear_unconfirmed", hostEpoch: epoch, sessionId: "other" });
    await join(original, owner);
    assert.equal(after.uncertainClear(), undefined);
    assert.equal(calls, 2);
  } catch (error) { primary = error; throw error; }
  finally { first.abort(); second.abort(); release({ status: "clear_unconfirmed", hostEpoch: epoch, sessionId: "other" }); await finish(originals, owner, primary); }
});

function acquire<T>(originals: Promise<unknown>[], start: () => Promise<T>): Promise<T> {
  let resolve!: (value: Promise<T>) => void;
  let reject!: (reason: unknown) => void;
  const observer = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  originals.push(observer); // Retain the acquisition/observer before calling transport or coordinator code.
  try { const original = start(); resolve(original); return original; }
  catch (error) { reject(error); return observer; }
}

async function join<T>(original: Promise<T>, owner: unknown): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const deadline = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(Object.assign(new Error("Notes deadline; original retained"), { original, owner })), 5000);
  });
  try { return await Promise.race([original, deadline]); }
  finally { if (timer !== undefined) clearTimeout(timer); }
}

async function finish(originals: readonly Promise<unknown>[], owner: unknown, primary: unknown): Promise<void> {
  try {
    const results = await join(Promise.allSettled(originals), owner);
    const failures = results.filter((value): value is PromiseRejectedResult => value.status === "rejected").map(value => value.reason);
    if (failures.length) throw new AggregateError(failures, "Original notes work failed");
  } catch (error) { throw new AggregateError(primary === undefined ? [error] : [primary, error], "Notes cleanup failed"); }
}
