import assert from "node:assert/strict";
import test from "node:test";
import { folderPickNotice, folderPickTimeoutMilliseconds, folderTitle, pickFolder, sameFolder } from "./folderPicker";

test("a folder pick returns the chosen folder and asks with the title and the trimmed start folder", async () => {
  const requests: unknown[] = [];
  const pick = await pickFolder(async (request, options) => { requests.push([request, options]); return { status: "ok", path: "C:\\code\\App" }; },
    "Add a project folder", "  C:\\code  ");
  assert.deepEqual(pick, { status: "ok", path: "C:\\code\\App" });
  assert.deepEqual(requests, [[{ title: "Add a project folder", initialDirectory: "C:\\code" }, { timeoutMilliseconds: folderPickTimeoutMilliseconds }]]);
  const blank: unknown[] = [];
  await pickFolder(async request => { blank.push(request); return { status: "canceled", path: null }; }, "Title", "   ");
  assert.deepEqual(blank, [{ title: "Title", initialDirectory: null }]);
});

test("a folder pick that chose nothing reports why, and anything else is a failed pick", async () => {
  for (const status of ["canceled", "busy", "unavailable", "failed"])
    assert.deepEqual(await pickFolder(async () => ({ status, path: null }), "Title", null), { status });
  for (const reply of [null, {}, { status: "ok" }, { status: "ok", path: "" }, { status: "ok", path: "  " }, { status: "ok", path: 7 },
    { status: "ok", path: "C:\\a\0b" }, { status: "ok", path: "x".repeat(4097) }, { status: "later", path: "C:\\a" }])
    assert.deepEqual(await pickFolder(async () => reply, "Title", null), { status: "failed" });
  assert.deepEqual(await pickFolder(async () => { throw new Error("closed"); }, "Title", null), { status: "failed" });
});

test("a folder pick without a dialog on this desktop, or whose dialog failed, is never silent", () => {
  assert.equal(folderPickNotice({ status: "unavailable" }), "No folder dialog is available on this desktop: drop the folder on the prompt instead.");
  assert.equal(folderPickNotice({ status: "failed" }), "The folder dialog could not be shown.");
  // Beside a field, the path can be typed instead.
  assert.equal(folderPickNotice({ status: "unavailable" }, "type"), "No folder dialog is available on this desktop: type the path of the folder instead.");
  assert.equal(folderPickNotice({ status: "failed" }, "type"), "The folder dialog could not be shown.");
  // The user closed the dialog, or another one is open: there is nothing to say.
  for (const status of ["canceled", "busy"] as const) assert.equal(folderPickNotice({ status }), null);
  assert.equal(folderPickNotice({ status: "ok", path: "/home/me" }), null);
});

test("a folder is attached under its name, a root under its path", () => {
  assert.equal(folderTitle("C:\\code\\App\\"), "App");
  assert.equal(folderTitle("/home/me/docs"), "docs");
  assert.equal(folderTitle("/"), "/");
});

test("two paths name the same folder whatever their case and trailing separator", () => {
  assert.equal(sameFolder("C:\\Code\\App", "c:\\code\\app\\"), true);
  assert.equal(sameFolder("/home/me/app/", "/home/me/app"), true);
  assert.equal(sameFolder("C:\\code\\App", "C:\\code\\App2"), false);
  assert.equal(sameFolder("", ""), false);
  assert.equal(sameFolder(" / ", "/"), false);
});
