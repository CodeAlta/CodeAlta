import assert from "node:assert/strict";
import test from "node:test";
import { spaceShowingProject, whenProjectKnown } from "./hostProjects";

test("a project added by alta is shown in the space shown when it has it, else in the space it joined, else in the default one", () => {
  const shown = [{ id: "a" }];
  assert.equal(spaceShowingProject("a", shown, "work", "personal", "default"), "work");
  assert.equal(spaceShowingProject("b", shown, "work", "personal", "default"), "personal");
  assert.equal(spaceShowingProject("b", shown, "work", null, "default"), "default");
});

test("the window waits for the project to be in the catalog it reads again, and gives up after a while", async () => {
  let projects: { id: string }[] | undefined;
  let waits = 0;
  const known = await whenProjectKnown("p", () => projects, async () => { if (++waits === 3) projects = [{ id: "p" }]; });
  assert.equal(known, true);
  assert.equal(waits, 3);
  assert.equal(await whenProjectKnown("q", () => projects, async () => {}, 5), false);
});
