import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { savedProjectSelection } from "./savedProjectSelection";

const shown = { id: "one", path: "C:/one", name: "One", archived: false };
const snapshot = (projects: typeof shown[], configured = true) => ({ projects, configured,
  sessions: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false }) as WorkspaceSnapshot;

test("saved project activation requires a current unique exact identity and unchanged display state", () => {
  assert.equal(savedProjectSelection(shown, snapshot([shown]))?.id, "one");
  const archived = { ...shown, archived: true };
  assert.equal(savedProjectSelection(archived, snapshot([archived]))?.archived, true);
  for (const current of [undefined, snapshot([], false), snapshot([]), snapshot([{ ...shown, path: "C:/moved" }]),
    snapshot([{ ...shown, id: "other" }]), snapshot([{ ...shown, name: "Renamed" }]),
    snapshot([{ ...shown, archived: true }]), snapshot([shown, shown]),
    snapshot([shown, { ...shown, id: "other" }]), snapshot([shown, { ...shown, path: "C:/other" }])])
    assert.equal(savedProjectSelection(shown, current), null);
});
