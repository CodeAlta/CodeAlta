import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { SessionRemoteControlResponse, SessionRuntimeStateEntry } from "#neoastra";
import { entryRemoteControl, providerHasRemoteControl, remoteControlOff, remoteControlView, sameRemoteControl } from "./remoteControl";
import { RemoteControlButton } from "./RemoteControlButton";

const link = "https://claude.ai/code/session_01Abc";
const entry = (remoteControl: unknown, changes: Partial<SessionRuntimeStateEntry> = {}): SessionRuntimeStateEntry => ({ attachmentGeneration: "9", isTerminated: false,
  isRetiring: false, activeRunId: null, backgroundTasks: [], queueDrainInProgress: false, providerId: "claude-code", providerKey: "claude-code", modelId: null,
  reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null, activity: null, remoteControl: remoteControl as SessionRemoteControlResponse, ...changes });

test("the Remote Control of a session is read as the host says it, and what is not one is off", () => {
  assert.deepEqual(remoteControlView({ status: "connected", sessionUrl: link, error: null }), { status: "connected", url: link, error: null });
  assert.deepEqual(remoteControlView({ status: "connecting", sessionUrl: null, error: null }), { status: "connecting", url: null, error: null });
  assert.deepEqual(remoteControlView({ status: "failed", sessionUrl: null, error: " It needs a claude.ai login. " }), { status: "failed", url: null, error: "It needs a claude.ai login." });
  // A link of another site is not opened from the page, and an error is said only of a failure.
  assert.equal(remoteControlView({ status: "connected", sessionUrl: "https://example.com/code", error: null }).url, null);
  assert.equal(remoteControlView({ status: "connected", sessionUrl: link, error: "stale" }).error, null);
  for (const other of [null, undefined, "connected", { status: "paused", sessionUrl: link, error: null }, { status: 3 }, { status: "off", sessionUrl: link, error: null }])
    assert.equal(remoteControlView(other as SessionRemoteControlResponse), remoteControlOff);
  assert.equal(Object.isFrozen(remoteControlView({ status: "connected", sessionUrl: link, error: null })), true);
  assert.equal(sameRemoteControl(remoteControlView({ status: "connected", sessionUrl: link, error: null }), { status: "connected", url: link, error: null }), true);
});

test("an attachment that ends, or a host that says nothing, has no Remote Control", () => {
  const on = { status: "connected", sessionUrl: link, error: null };
  assert.equal(entryRemoteControl(entry(on)).status, "connected");
  for (const without of [null, undefined, entry(undefined), entry(on, { isTerminated: true }), entry(on, { isRetiring: true })])
    assert.equal(entryRemoteControl(without), remoteControlOff);
});

test("Remote Control is offered for Claude Code only", () => {
  assert.equal(providerHasRemoteControl("claude-code"), true);
  for (const other of ["codex", "anthropic", "", null, undefined]) assert.equal(providerHasRemoteControl(other), false);
});

test("the button says where Remote Control stands, by its label and by its color", () => {
  const render = (status: "off" | "connecting" | "connected" | "failed") => renderToStaticMarkup(
    <RemoteControlButton state={status === "off" ? remoteControlOff : { status, url: status === "connected" ? link : null, error: null }}
      onSet={async () => "ok"} onOpenLink={() => {}} />);
  for (const [status, label] of [["off", "Remote Control: off"], ["connecting", "Remote Control: connecting"], ["connected", "Remote Control: on"], ["failed", "Remote Control: failed"]] as const) {
    const markup = render(status);
    assert.match(markup, new RegExp(`data-remote-control="${status}"`));
    assert.match(markup, new RegExp(`aria-label="${label}"`));
  }
});
