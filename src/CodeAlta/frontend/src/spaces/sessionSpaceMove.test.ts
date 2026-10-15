import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceMoveToSpaceRequest, WorkspaceMoveToSpaceResponse } from "#neoastra";
import { spaceMoveTargets } from "./MoveToSpaceDialog";
import { moveSessionToSpace, spaceMoveMessage } from "./sessionSpaceMove";
import { readSpaces } from "./spaces";

const epoch = "12345678-1234-1234-1234-123456789abc";
const reply = (status: string, changes: Partial<WorkspaceMoveToSpaceResponse> = {}): WorkspaceMoveToSpaceResponse =>
  ({ status, hostEpoch: epoch, sessionId: "s1", spaceId: "work", moved: [], warnings: [], ...changes });

test("a session is given another space through the host, which says what moved", async () => {
  const requests: WorkspaceMoveToSpaceRequest[] = [];
  let answer = reply("ok", { moved: ["child", "s1"], warnings: ["The provider did not stop cleanly."] });
  const invoke = async (request: WorkspaceMoveToSpaceRequest) => { requests.push(request); return answer; };

  assert.deepEqual(await moveSessionToSpace(invoke, epoch, "s1", "work"), { kind: "moved", moved: ["child", "s1"], warnings: ["The provider did not stop cleanly."] });
  assert.deepEqual(requests, [{ expectedHostEpoch: epoch, sessionId: "s1", spaceId: "work" }]);

  answer = reply("session_in_use");
  assert.deepEqual(await moveSessionToSpace(invoke, epoch, "s1", "work"), { kind: "error", code: "session_in_use" });
  // An answer about another session or another space confirms nothing.
  answer = reply("ok", { spaceId: "personal" });
  assert.deepEqual(await moveSessionToSpace(invoke, epoch, "s1", "work"), { kind: "error", code: "move_unconfirmed" });
  assert.deepEqual(await moveSessionToSpace(async () => { throw new Error("gone"); }, epoch, "s1", "work"), { kind: "error", code: "move_unconfirmed" });
  assert.deepEqual(await moveSessionToSpace(invoke, null, "s1", "work"), { kind: "error", code: "unconfigured" });

  assert.equal(spaceMoveMessage("space_missing"), "The space is gone. Choose another one.");
  assert.match(spaceMoveMessage("session_in_use"), /idle/);
});

test("the space a session is in is not offered as the one to move it to", () => {
  const spaces = readSpaces([
    { id: "default", name: "Default", description: null, icon: null, color: null, isDefault: true, projectIds: [], file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] },
    { id: "work", name: "Work", description: null, icon: null, color: null, isDefault: false, projectIds: [], file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] },
  ]);
  assert.deepEqual(spaceMoveTargets(spaces, "work").map(space => space.id), ["default"]);
  assert.deepEqual(spaceMoveTargets(spaces, "default").map(space => space.id), ["work"]);
});
