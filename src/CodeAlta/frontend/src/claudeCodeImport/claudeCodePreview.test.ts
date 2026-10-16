import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryEntry, WorkspaceClaudeCodeSessionPreviewRequest, WorkspaceClaudeCodeSessionPreviewResponse, WorkspaceSession } from "#neoastra";
import { createLocalDrafts, draftStorageKey } from "../promptDraft";
import { createImageDrafts } from "../promptImages";
import { previewBannerText, previewHeldText, previewPlaceholder, previewReader, previewSessionId, previewUnavailableMessage, type ClaudeCodePreviewStatus } from "./claudeCodePreview";
import { clearPreviewDraft, continuePreview, handOffPreviewPrompt, previewDraftScope, previewDraftStorageKey, previewImageKey, type HandOffRefusal, type PreviewPrompt } from "./previewContinuation";

const epoch = "11111111-1111-4111-8111-111111111111";
const cli = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11";
const project = { id: "p1", path: "C:\\dev\\p1" };
const entry = (offset: string, text: string): HistoryEntry => ({ offset, eventType: "contentCompleted", providerId: "claude-code", sessionId: previewSessionId(cli), runId: "external:r1",
  timestamp: "2026-10-15T08:00:00Z", kind: "Assistant", phase: null, contentId: `c${offset}`, activityId: null, parentActivityId: null, interactionId: null, name: null, text,
  details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, files: null, tool: null, images: null, sourceSessionId: null, prompt: null });
const reply = (fields: Partial<WorkspaceClaudeCodeSessionPreviewResponse> = {}): WorkspaceClaudeCodeSessionPreviewResponse => ({ status: "ok", hostEpoch: epoch,
  projectId: project.id, projectPath: project.path, cliSessionId: cli, sessionId: null, title: "Fix the parser",
  page: { status: "ok", entries: [entry("2", "Newest")], next: { version: 2, sessionId: previewSessionId(cli), length: "900", lastWriteUtcTicks: "7", offset: "2" }, tailOmitted: false },
  ...fields });

test("the preview reads the pages of the conversation as the timeline reads those of a journal, and says what it found", async () => {
  const asked: WorkspaceClaudeCodeSessionPreviewRequest[] = [];
  const answers: WorkspaceClaudeCodeSessionPreviewResponse[] = [reply(), reply({ page: { status: "ok", entries: [entry("1", "Oldest")], next: null, tailOmitted: false } })];
  const reports: ClaudeCodePreviewStatus[] = [];
  const read = previewReader(async request => { asked.push(request); return answers.shift()!; }, epoch, project, cli, status => reports.push(status));

  const newest = await read({ sessionId: previewSessionId(cli), cursor: null });
  assert.deepEqual([newest.status, newest.entries.map(value => value.text), newest.revision, newest.sources], ["ok", ["Newest"], null, []]);
  const older = await read({ sessionId: previewSessionId(cli), cursor: newest.next });
  assert.deepEqual([older.entries.map(value => value.text), older.next], [["Oldest"], null]);
  assert.deepEqual(asked.map(request => [request.expectedHostEpoch, request.projectId, request.projectPath, request.cliSessionId, request.cursor?.offset ?? null, request.spaceId]),
    [[epoch, "p1", "C:\\dev\\p1", cli, null, null], [epoch, "p1", "C:\\dev\\p1", cli, "2", null]], "The exact project and conversation, the cursor of the page after.");
  // The space the window shows reads it with its provider.
  const spaced: WorkspaceClaudeCodeSessionPreviewRequest[] = [];
  await previewReader(async request => { spaced.push(request); return reply(); }, epoch, { ...project, spaceId: "business" }, cli, () => { })({ sessionId: previewSessionId(cli), cursor: null });
  assert.equal(spaced[0]?.spaceId, "business");
  assert.deepEqual(reports, [{ kind: "shown", title: "Fix the parser" }, { kind: "shown", title: "Fix the parser" }]);

  // A conversation a session continues already: that session is opened.
  reports.length = 0;
  answers.push(reply({ status: "existing", sessionId: "s-old" }));
  assert.equal((await read({ sessionId: previewSessionId(cli), cursor: null })).status, "existing");
  assert.deepEqual(reports, [{ kind: "existing", sessionId: "s-old" }]);
  // Why it is not shown; a page that can be read again is left to the timeline to retry.
  reports.length = 0;
  for (const status of ["too_large", "not_found", "history_changed", "read_failed"]) {
    answers.push(reply({ status, page: { status, entries: [], next: null, tailOmitted: false } }));
    assert.equal((await read({ sessionId: previewSessionId(cli), cursor: null })).status, status);
  }
  assert.deepEqual(reports, [{ kind: "unavailable", code: "too_large" }, { kind: "unavailable", code: "not_found" }]);
  // A reply about another conversation, project or host is no page; nor is a read of another timeline.
  for (const other of [reply({ cliSessionId: "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e12" }), reply({ projectPath: "C:\\dev\\p2" }), reply({ hostEpoch: "other" })]) {
    answers.push(other);
    const page = await read({ sessionId: previewSessionId(cli), cursor: null });
    assert.deepEqual([page.status, page.entries.length], ["read_failed", 0]);
  }
  const before = asked.length;
  assert.equal((await read({ sessionId: "s-other", cursor: null })).status, "invalid_cursor");
  assert.equal(asked.length, before, "Nothing is asked for another timeline.");
  const failing = previewReader(async () => { throw new Error("lost"); }, epoch, project, cli, () => { });
  assert.equal((await failing({ sessionId: previewSessionId(cli), cursor: null })).status, "read_failed");
});

test("the banner says where the conversation was started and who works in it now; the prompt says what it continues", () => {
  assert.equal(previewBannerText("vscode"), "Claude Code conversation started in VS Code, not in CodeAlta yet. Your first message continues it here.");
  assert.equal(previewBannerText("terminal"), "Claude Code conversation started in a terminal, not in CodeAlta yet. Your first message continues it here.");
  assert.match(previewBannerText("other"), /another program/);
  assert.equal(previewPlaceholder("vscode"), "Continue this VS Code conversation in CodeAlta…");
  assert.equal(previewPlaceholder("terminal"), "Continue this terminal conversation in CodeAlta…");
  assert.equal(previewPlaceholder("unknown"), "Continue this Claude Code conversation in CodeAlta…");
  assert.equal(previewHeldText(null), null);
  assert.equal(previewHeldText({ origin: "vsCode", status: "idle" }), null, "a conversation merely open in a VS Code panel");
  assert.equal(previewHeldText({ origin: "vsCode", status: "busy" }), "In use in VS Code now: your first message will ask whether to wait until it is free.");
  assert.match(previewHeldText({ origin: "terminal", status: null })!, /terminal/, "an unreadable entry counts as in use");
  assert.match(previewHeldText({ origin: "backgroundSession", status: "busy" })!, /another Claude process/);
  assert.match(previewUnavailableMessage("too_large"), /too large/);
  assert.match(previewUnavailableMessage("not_found"), /no longer be shown/);
});

const session = (id: string): WorkspaceSession => ({ id, title: "Fix the parser", fullTitle: "Fix the parser", fullTitleTruncated: false, workspacePath: "C:\\dev\\p1",
  scopeKind: "project", projectId: "p1", messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false,
  forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, createdAt: null, updatedAt: "2026-10-15T08:00:00Z",
  parentSessionId: null, lineageIssue: null, providerKey: "claude-code", spaceId: null, archived: false, archivedAt: null });
const storage = () => {
  const values = new Map<string, string>();
  return { values, read: (key: string) => values.get(key) ?? null, write: (key: string, value: string) => { values.set(key, value); } };
};
const png = { title: "Image 1", mediaType: "image/png", base64: "iVBORw0KGgo=" };
const prompt = (text: string, images: PreviewPrompt["images"] = []): PreviewPrompt =>
  ({ text, images, imageKey: previewImageKey(epoch, "p1", cli), scope: previewDraftScope("p1", cli) });

test("the first message of a preview becomes the draft of the session that continues the conversation, never over a draft of its own", () => {
  const kept = storage();
  assert.equal(handOffPreviewPrompt(kept, createImageDrafts(), epoch, session("s-new"), prompt("Run the tests."), false), true);
  assert.equal(kept.read(draftStorageKey("s-new")), "Run the tests.", "The session holds it: a failed send leaves it there as its draft.");
  assert.equal(handOffPreviewPrompt(kept, createImageDrafts(), epoch, session("s-new"), prompt("Another"), false), false, "A draft of the session is never replaced.");
  assert.equal(kept.read(draftStorageKey("s-new")), "Run the tests.");
  assert.equal(handOffPreviewPrompt(storage(), createImageDrafts(), epoch, session("s-busy"), prompt("Run"), true), false, "Nor given while the session sends.");
  assert.equal(handOffPreviewPrompt(storage(), createImageDrafts(), epoch, session("s-empty"), prompt("  "), false), false);

  // The images go to the composer of the session, under its own key.
  const images = createImageDrafts();
  const withImages = prompt("Look at this.", [png]);
  assert.ok(images.replace(withImages.imageKey, images.get(withImages.imageKey), withImages.images));
  const pictured = storage();
  assert.equal(handOffPreviewPrompt(pictured, images, epoch, session("s-pictured"), { ...withImages, images: images.get(withImages.imageKey) }, false), true);
  assert.deepEqual(images.get(JSON.stringify([epoch, "s-pictured", "p1", "C:\\dev\\p1"])).map(image => image.title), ["Image 1"]);
  assert.equal(pictured.read(draftStorageKey("s-pictured")), "Look at this.");

  // Once given, the draft of the preview is empty again, in the window and in storage.
  const drafts = createLocalDrafts();
  const removed: string[] = [];
  drafts.edit(withImages.scope, "Look at this.");
  clearPreviewDraft(drafts, withImages.scope, key => removed.push(key));
  assert.equal(drafts.peek(withImages.scope)?.text, "");
  assert.deepEqual(removed, [previewDraftStorageKey(withImages.scope)]);
});

test("continuing a preview imports it, then opens the session with the first message to send; a message not given to it stays under the preview", async () => {
  const steps = (imported: Awaited<ReturnType<Parameters<typeof continuePreview>[0]["importConversation"]>>, found: WorkspaceSession | null, refused: HandOffRefusal | null = null) => {
    const calls: string[] = [];
    return { calls, steps: {
      importConversation: async () => { calls.push("import"); return imported; },
      findSession: async (id: string) => { calls.push(`find:${id}`); return found; },
      handOff: (value: WorkspaceSession, message: PreviewPrompt) => { calls.push(`hand:${value.id}:${message.text}+${message.images.length}`); return refused; },
      open: (value: WorkspaceSession, send: Readonly<{ text: string; images: number }> | null) => { calls.push(`open:${value.id}:${send ? `${send.text}+${send.images}` : "nothing"}`); },
    } };
  };
  // The first send: imported, the message given to the new session, which opens and sends it.
  const send = steps({ kind: "opened", sessionId: "s-new", existing: false }, session("s-new"));
  assert.deepEqual(await continuePreview(send.steps, prompt("Run the tests.")), { kind: "opened", sessionId: "s-new", existing: false, sent: true });
  assert.deepEqual(send.calls, ["import", "find:s-new", "hand:s-new:Run the tests.+0", "open:s-new:Run the tests.+0"]);

  // Continue in CodeAlta: imported and opened, nothing to send.
  const button = steps({ kind: "opened", sessionId: "s-new", existing: false }, session("s-new"));
  assert.deepEqual(await continuePreview(button.steps, null), { kind: "opened", sessionId: "s-new", existing: false, sent: false });
  assert.deepEqual(button.calls, ["import", "find:s-new", "open:s-new:nothing"]);

  // A session continues it already, but does not take the message: the preview is not left, and keeps the message with
  // its images; the window is told why (a draft of its own, a prompt it sends, a host that changed).
  for (const reason of ["draft", "sending", "stale"] as const) {
    const kept = steps({ kind: "opened", sessionId: "s-old", existing: true }, session("s-old"), reason);
    assert.deepEqual(await continuePreview(kept.steps, prompt("Look at this.", [png])), { kind: "kept", sessionId: "s-old", existing: true, reason });
    assert.deepEqual(kept.calls, ["import", "find:s-old", "hand:s-old:Look at this.+1"], "nothing is opened in the place of the preview");
  }
  // Images alone are a message too.
  const pictures = steps({ kind: "opened", sessionId: "s-old", existing: true }, session("s-old"), "draft");
  assert.deepEqual(await continuePreview(pictures.steps, prompt("", [png])), { kind: "kept", sessionId: "s-old", existing: true, reason: "draft" });

  // The import fails, or the session is not in the list read after: nothing is opened, and the message stays under the preview.
  const failed = steps({ kind: "error", code: "busy" }, null);
  assert.deepEqual(await continuePreview(failed.steps, prompt("Run the tests.")), { kind: "import_failed", code: "busy" });
  assert.deepEqual(failed.calls, ["import"]);
  const unlisted = steps({ kind: "opened", sessionId: "s-new", existing: false }, null);
  assert.deepEqual(await continuePreview(unlisted.steps, prompt("Run the tests.")), { kind: "unopened", sessionId: "s-new" });
  assert.deepEqual(unlisted.calls, ["import", "find:s-new"]);
});
