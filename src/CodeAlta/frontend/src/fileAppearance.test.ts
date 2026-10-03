import assert from "node:assert/strict";
import test from "node:test";
import { fileAppearance, splitProjectPath } from "./fileAppearance";

test("known file types get their language tone; names win over extensions; anything else is neutral", () => {
  assert.deepEqual(fileAppearance("src", true), { icon: "folder", tone: "gold" });
  assert.deepEqual(fileAppearance("src/Program.CS", false), { icon: "fileCode", tone: "purple" });
  assert.equal(fileAppearance("a/b/app.tsx", false).tone, "blue");
  assert.deepEqual(fileAppearance("package.json", false), { icon: "fileJson", tone: "yellow" });
  assert.deepEqual(fileAppearance("README.md", false), { icon: "fileText", tone: "blue" });
  assert.deepEqual(fileAppearance("build.ps1", false), { icon: "fileTerminal", tone: "blue" });
  assert.equal(fileAppearance("main.py", false).tone, "teal");
  assert.equal(fileAppearance("logo.png", false).icon, "fileImage");
  assert.deepEqual(fileAppearance("docker/Dockerfile", false), { icon: "fileCode", tone: "blue" });
  assert.deepEqual(fileAppearance("LICENSE", false), { icon: "fileGeneric", tone: "muted" });
  assert.deepEqual(fileAppearance(".gitignore", false), { icon: "fileGeneric", tone: "muted" });
  assert.deepEqual(fileAppearance("data.unknownext", false), { icon: "fileGeneric", tone: "muted" });
});

test("a path splits into its base name and parent folder", () => {
  assert.deepEqual(splitProjectPath("src/components/Button.tsx"), { name: "Button.tsx", parent: "src/components" });
  assert.deepEqual(splitProjectPath("README.md"), { name: "README.md", parent: "" });
});
