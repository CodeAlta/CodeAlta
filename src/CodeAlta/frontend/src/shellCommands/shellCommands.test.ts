import assert from "node:assert/strict";
import test from "node:test";
import { createShellCommands, plainOutput, shellAttachment, shellCommandConcurrency, shellOutputLimits, shellOutputText, shellOutputMediaType, type ShellCommandEntry, type ShellCommandEvent } from "./shellCommands";

const decode = (base64: string) => new TextDecoder().decode(Uint8Array.from(atob(base64), value => value.charCodeAt(0)));
const ended = (store: ReturnType<typeof createShellCommands>, session: string) =>
  new Promise<ShellCommandEntry>(resolve => { const stop = store.subscribe(() => {
    const entry = store.view(session).entries.at(-1);
    if (entry && entry.state !== "running") { stop(); resolve(entry); }
  }); });

test("a command streams its output, ends with its exit code and is told to whoever ran it", async () => {
  const calls: string[] = [];
  const store = createShellCommands(async (session, command) => {
    calls.push(`${session}:${command}`);
    return (async function* (): AsyncGenerator<ShellCommandEvent> {
      yield { kind: "started", value: "C:\\work" };
      yield { kind: "output", value: "On branch main\n" };
      yield { kind: "output", value: "nothing to commit\n" };
      yield { kind: "exit", exitCode: 0, omitted: 0 };
    })();
  });
  let told: ShellCommandEntry | null = null;
  const done = ended(store, "a");
  const id = store.run("a", "  git status  ", entry => { told = entry; })!;
  assert.equal(store.view("a").shown, id, "the panel shows the command as it runs");
  assert.equal(store.runningCount("a"), 1);
  const entry = await done;
  // Whoever ran it is told once the stream is closed, just after the panel shows how it ended.
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(entry.output, "On branch main\nnothing to commit\n");
  assert.deepEqual([entry.state, entry.exitCode, entry.folder], ["done", 0, "C:\\work"]);
  assert.equal(told, entry);
  assert.deepEqual(calls, ["a:git status"]);
  assert.deepEqual(store.view("b").entries, [], "each session has its own commands");
  assert.equal(store.runningCount("a"), 0);
});

test("a command that cannot run says why, and one stopped is ended on the host", async () => {
  const signals: AbortSignal[] = [];
  const store = createShellCommands(async (_session, command, signal) => {
    signals.push(signal);
    if (command === "bad") return (async function* (): AsyncGenerator<ShellCommandEvent> { yield { kind: "error", status: "missing_folder", value: "C:\\gone" }; })();
    return (async function* (): AsyncGenerator<ShellCommandEvent> {
      yield { kind: "started", value: "C:\\work" };
      await new Promise(resolve => signal.addEventListener("abort", resolve));
    })();
  });
  const failed = ended(store, "a");
  store.run("a", "bad");
  assert.deepEqual((await failed).failure, { status: "missing_folder", message: "C:\\gone" });
  const id = store.run("a", "watch")!;
  store.stop("a", id);
  assert.equal(signals[1].aborted, true);
  assert.equal(store.view("a").entries.at(-1)!.state, "stopped");
  store.remove("a", id);
  assert.deepEqual(store.view("a").entries.map(entry => entry.command), ["bad"]);
  assert.equal(store.view("a").listing, true, "removing the command shown lists the others");
  // Each command that runs holds a channel of the window: a session runs a few at once, not more.
  for (let index = 0; index < shellCommandConcurrency; index++) assert.notEqual(store.run("a", `watch ${index}`), null);
  assert.equal(store.run("a", "one more"), null);
  assert.notEqual(store.run("b", "elsewhere"), null, "another session runs its own");
  store.dispose();
});

test("the prompt mode is by session, and the commands are recalled once in a row", async () => {
  const store = createShellCommands(async () => (async function* (): AsyncGenerator<ShellCommandEvent> { yield { kind: "exit", exitCode: 0 }; })());
  let changes = 0;
  store.subscribe(() => changes++);
  store.setMode("a", true);
  store.setMode("a", true);
  assert.equal(changes, 1, "a mode that does not change tells nobody");
  assert.equal(store.mode("a"), true);
  assert.equal(store.mode("b"), false);
  for (const command of ["ls", "ls", "git status"]) store.run("a", command);
  assert.deepEqual(store.commands("a"), ["ls", "git status"]);
  store.close("a");
  assert.equal(store.isOpen("a"), false);
  await new Promise(resolve => setTimeout(resolve, 10));
  store.clear("a");
  assert.deepEqual(store.view("a").entries, []);
});

test("the output given to the model is plain text, with its start and its end when it is long", () => {
  assert.equal(plainOutput("\x1b[32mok\x1b[0m\r\n10%\r50%\r100%\n"), "ok\n100%\n");
  const entry: ShellCommandEntry = { id: 1, command: "type C:\\a/b.txt", output: "x".repeat(shellOutputLimits.head + shellOutputLimits.tail + 5), omitted: 0, tail: "",
    state: "done", exitCode: 2, folder: "C:\\work", startedAt: "2026-11-13T10:00:00Z", endedAt: "2026-11-13T10:00:01Z", failure: null };
  const file = shellAttachment(entry);
  assert.equal(file.mediaType, shellOutputMediaType);
  assert.equal(file.title, "$ type C:⧵a∕b.txt", "a title is a file name: no separator of folders");
  const text = decode(file.base64);
  assert.ok(text.startsWith("The user ran this command in a shell, in C:\\work; it exited with code 2.\n\n$ type C:\\a/b.txt\n"), text.slice(0, 120));
  assert.ok(text.includes("[… 5 characters omitted …]"));
  assert.equal(shellAttachment(entry).base64, file.base64, "the same command gives the same attachment");
  assert.ok(decode(shellAttachment({ ...entry, output: "", state: "stopped" }).base64).includes("the user stopped it before it ended.\n\n$ type C:\\a/b.txt\n(no output)"));
});

test("an output the host did not stream whole shows and gives the model its real end", () => {
  const base: ShellCommandEntry = { id: 2, command: "dotnet test", output: "a".repeat(40_000), omitted: 0, tail: "", state: "done", exitCode: 1,
    folder: null, startedAt: "2026-11-13T10:00:00Z", endedAt: "2026-11-13T10:00:01Z", failure: null };
  // The host sent all of what it left out: the text is whole.
  assert.equal(shellOutputText({ ...base, output: "start\n", omitted: 4, tail: "end\n" }), "start\nend\n");
  // It sent only the end of it: a line counts the characters in between.
  const long = { ...base, omitted: 100_000, tail: "Failed: 3 tests\n" };
  assert.equal(shellOutputText(long), `${"a".repeat(40_000)}\n[… ${100_000 - 16} characters …]\nFailed: 3 tests\n`);
  const text = decode(shellAttachment(long).base64);
  assert.ok(text.endsWith("Failed: 3 tests\n\n"), text.slice(-80));
  assert.ok(text.includes(`[… ${40_000 - shellOutputLimits.head + 100_000 - 16} characters omitted …]`));
});
