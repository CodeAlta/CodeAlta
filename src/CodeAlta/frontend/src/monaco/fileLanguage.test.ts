import assert from "node:assert/strict";
import test from "node:test";
import { editorLanguages, fileLanguage, languageExtension, languageName } from "./fileLanguage";

test("the extension chooses the editor language, ignoring case and folders", () => {
  for (const [path, language] of [["src/main.tsx", "typescript"], ["a/b/index.MJS", "javascript"], ["Program.cs", "csharp"],
    ["src/App.csproj", "xml"], ["Directory.Build.props", "xml"], ["package.json", "json"], ["tsconfig.JSON", "json"], ["readme.md", "markdown"],
    [".alta/config.toml", "toml"], ["settings.ini", "ini"], ["ci/build.yml", "yaml"], ["tool.py", "python"], ["lib.rs", "rust"], ["main.go", "go"], ["native/x.h", "cpp"],
    ["run.sh", "shell"], ["build.ps1", "powershell"], ["run.cmd", "bat"], ["schema.sql", "sql"], ["site.scss", "scss"], ["page.html", "html"],
    ["Views/Index.cshtml", "razor"], ["main.tf", "hcl"], ["src\\win\\path.kt", "kotlin"], ["lib/app.ex", "elixir"], ["core.clj", "clojure"],
    ["token.sol", "solidity"], ["fix.patch", "diff"], ["rules.mk", "makefile"], ["App.vue", "html"], ["docs/index.rst", "restructuredtext"]] as const)
    assert.equal(fileLanguage(path), language, path);
});

test("well-known file names win over extensions; unknown or missing extensions are plain text", () => {
  assert.equal(fileLanguage("Dockerfile"), "dockerfile");
  assert.equal(fileLanguage("docker/Dockerfile.release"), "dockerfile");
  assert.equal(fileLanguage("Makefile"), "makefile");
  assert.equal(fileLanguage("src/GNUmakefile"), "makefile");
  assert.equal(fileLanguage(".env"), "ini");
  assert.equal(fileLanguage(".editorconfig"), "ini");
  assert.equal(fileLanguage(".gitignore"), "plaintext");
  assert.equal(fileLanguage("LICENSE"), "plaintext");
  assert.equal(fileLanguage("notes.txt"), "plaintext");
  assert.equal(fileLanguage("archive.unknownext"), "plaintext");
  assert.equal(fileLanguage("folder.ts/file"), "plaintext");
  assert.equal(fileLanguage(""), "plaintext");
});

test("every mapped language is one the editor can register", () => {
  for (const path of ["a.ts", "a.js", "a.cs", "a.fs", "a.vb", "a.xml", "a.html", "a.css", "a.less", "a.yaml", "a.java", "a.scala", "a.swift",
    "a.mm", "a.rb", "a.php", "a.lua", "a.pl", "a.r", "a.dart", "a.graphql", "a.proto", "a.bicep", "a.ini", "a.md", "a.json"])
    assert.ok(editorLanguages.includes(fileLanguage(path)) && fileLanguage(path) !== "plaintext", path);
  assert.equal(new Set(editorLanguages).size, editorLanguages.length);
});

test("a new file of a language is saved with the usual extension of that language", () => {
  assert.deepEqual((["plaintext", "markdown", "typescript", "csharp", "python", "yaml", "cpp", "shell", "dockerfile", "rust"] as const).map(languageExtension),
    ["txt", "md", "ts", "cs", "py", "yml", "cpp", "sh", "txt", "rs"]);
  // The extension names its language again, for every language an extension names: the file opens as it was written.
  for (const language of editorLanguages) {
    const extension = languageExtension(language);
    if (extension !== "txt" || language === "plaintext") assert.equal(fileLanguage(`untitled-1.${extension}`), language, language);
  }
});

test("a language has a name where it is chosen", () => {
  assert.deepEqual((["csharp", "cpp", "plaintext", "typescript", "rust", "objective-c"] as const).map(languageName), ["C#", "C/C++", "Plain Text", "TypeScript", "Rust", "Objective-C"]);
  assert.ok(editorLanguages.every(language => languageName(language).length > 0));
});
