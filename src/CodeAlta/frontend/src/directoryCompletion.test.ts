import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceDirectoryCompletionRequest, WorkspaceDirectoryCompletionResponse } from "#neoastra";
import { folderCompletionRequest, projectFolderCompletion } from "./directoryCompletion";

const epoch = "12345678-1234-1234-1234-123456789abc";
const request: WorkspaceDirectoryCompletionRequest = { expectedHostEpoch: epoch, directoryPath: "C:\\Work\\", prefix: "Al" };
const reply: WorkspaceDirectoryCompletionResponse = { status: "complete", hostEpoch: epoch, directoryPath: request.directoryPath,
  prefix: request.prefix, directories: ["C:\\Work\\Alpha"], entriesVisited: 1, omittedUnsafeEntries: false };

test("folder suggestion input splits literal absolute draft without expansion or scanning roots", () => {
  assert.deepEqual(folderCompletionRequest("C:\\Work\\Al", epoch), request);
  assert.deepEqual(folderCompletionRequest("C:\\Work\\", epoch), { ...request, prefix: "" });
  assert.deepEqual(folderCompletionRequest("/work/Al", epoch), { ...request, directoryPath: "/work/", prefix: "Al" });
  for (const draft of ["", "Al", "~/Al", "./Al", "C:\\Al", "/Al", "\\\\server\\Al", "//server/Al",
    "C:/Work/Al", "C:\\Work\\..\\Al", "C:\\Work\\\\Al", "C:\\Work\\\ud800", "C:\\Work\\" + "a".repeat(129)])
    assert.equal(typeof folderCompletionRequest(draft, epoch), "string", draft);
});

test("folder suggestion results refuse foreign identity, malformed budgets and non-direct children", () => {
  assert.deepEqual(projectFolderCompletion(reply, request)?.directories, reply.directories);
  const invalid: Partial<WorkspaceDirectoryCompletionResponse>[] = [
    { hostEpoch: "different" }, { directoryPath: "C:\\Foreign\\" }, { prefix: "A" }, { status: "unknown" },
    { entriesVisited: 130 }, { entriesVisited: -1 }, { omittedUnsafeEntries: null as unknown as boolean },
    { directories: ["C:\\Work-sibling\\Alpha"] }, { directories: ["C:\\Work\\Nested\\Alpha"] },
    { directories: ["C:\\Work\\Alpha/child"] },
    { directories: ["C:\\Work\\Other"] }, { entriesVisited: 0 }, { omittedUnsafeEntries: true },
    { directories: ["D:\\Other\\Alpha"] }, { directories: ["C:\\Work\\Alpha", "C:\\Work\\Alpha"] },
    { directories: Array(17).fill("C:\\Work\\Alpha") }, { directories: ["C:\\Work\\\ud800"] },
    { directories: ["C:\\Work\\" + "a".repeat(1024)] },
    { status: "busy", directories: reply.directories },
  ];
  for (const change of invalid) assert.equal(projectFolderCompletion({ ...reply, ...change }, request), undefined, JSON.stringify(change));
  assert.equal(projectFolderCompletion({ ...reply, status: "incomplete", directories: [] }, request)?.status, "incomplete");
  assert.equal(projectFolderCompletion({ ...reply, status: "busy", directories: [] }, request)?.status, "busy");
});
