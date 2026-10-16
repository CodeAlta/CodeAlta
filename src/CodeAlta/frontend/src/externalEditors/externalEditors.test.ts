import assert from "node:assert/strict";
import test from "node:test";
import type { ExternalEditorsResponse } from "#neoastra";
import { createExternalOpener, failure, openInFileManagerKey, type ExternalOpenerApi } from "./externalEditors";

const reply = (current: { id: string; name: string } | null, platform = "windows"): ExternalEditorsResponse => ({ status: "ok", platform, editors: [],
  current: current && { ...current, program: "editor" }, chosen: null, customProgram: null, customArguments: null });

function fake(answers: ExternalEditorsResponse[], statuses: Record<string, string> = {}) {
  const calls: string[] = [], failures: string[] = [];
  const api: ExternalOpenerApi = {
    list: async refresh => { calls.push(`list ${refresh}`); return answers.shift() ?? reply(null); },
    openFolder: async (target, projectId, sessionId) => { calls.push(`folder ${target} ${projectId} ${sessionId}`); return statuses[target] ?? "ok"; },
    openEntry: async (target, projectId, path, line) => { calls.push(`entry ${target} ${projectId} ${path} ${line}`); return statuses[target] ?? "ok"; },
    openWorktree: async (target, projectId, path) => { calls.push(`worktree ${target} ${projectId} ${path}`); return statuses[target] ?? "ok"; },
    failed: message => { failures.push(message); },
  };
  return { api, calls, failures };
}

test("the editor is asked for once, when something first needs it, and again when it changes", async () => {
  const host = fake([reply({ id: "vscode", name: "Visual Studio Code" }), reply({ id: "cursor", name: "Cursor" })]);
  const opener = createExternalOpener(host.api);
  assert.deepEqual(opener.state(), { editor: undefined, platform: null });
  assert.deepEqual(host.calls, []);
  let changes = 0;
  opener.subscribe(() => changes++);
  opener.ensure();
  opener.ensure();
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.deepEqual(opener.state(), { editor: { id: "vscode", name: "Visual Studio Code" }, platform: "windows" });
  assert.deepEqual(host.calls, ["list false"]);
  await opener.reload();
  assert.equal(opener.state().editor?.id, "cursor");
  assert.equal(changes, 2);
  // What a page of Settings was answered is taken as it is.
  opener.accept(reply(null, "macos"));
  assert.deepEqual(opener.state(), { editor: null, platform: "macos" });
});

test("a host that cannot answer leaves no editor, and the actions stay hidden", async () => {
  const opener = createExternalOpener({ ...fake([]).api, list: async () => { throw new Error("gone"); } });
  opener.ensure();
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(opener.state().editor, null);
});

test("what is opened is named to the host, and a refusal is told to the user", async () => {
  const host = fake([reply({ id: "vscode", name: "Visual Studio Code" })], { editor: "no_editor", explorer: "failed" });
  const opener = createExternalOpener(host.api);
  await opener.openFolder("editor", "p1", "s1");
  await opener.openEntry("explorer", "p1", "src/a.ts", 4);
  await opener.openWorktree("editor", "p1", "C:/wt");
  assert.deepEqual(host.calls.filter(call => !call.startsWith("list")), ["folder editor p1 s1", "entry explorer p1 src/a.ts 4", "worktree editor p1 C:/wt"]);
  assert.deepEqual(host.failures, ["No external editor was found. Choose one in Settings > General.", "The file manager could not be opened.",
    "No external editor was found. Choose one in Settings > General."]);
  // An editor that is gone is looked for again.
  assert.ok(host.calls.includes("list true"));
});

test("the failures and the file manager are named for the user", () => {
  assert.equal(failure("editor", "refused"), "The custom editor cannot be started: choose a program that exists and is not a batch file.");
  assert.equal(failure("editor", "failed"), "The external editor could not be started.");
  assert.equal(failure("editor", "not_found"), "The folder could not be opened.");
  assert.equal(failure("explorer", "worktree_missing"), "The folder could not be opened.");
  assert.equal(openInFileManagerKey("windows"), "Open in File Explorer");
  assert.equal(openInFileManagerKey("macos", true), "Open {name} in Finder");
  assert.equal(openInFileManagerKey("linux"), "Open in the file manager");
  assert.equal(openInFileManagerKey(null), "Open in the file manager");
});
