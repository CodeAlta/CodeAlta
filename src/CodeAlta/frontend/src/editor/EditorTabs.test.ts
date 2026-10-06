import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { locales, translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { EditorTabs } from "./EditorTabs";
import { deletionWording } from "./UnsavedDialogs";

const never = () => assert.fail("rendering must not dispatch");

test("a tab shows its file, the folder of a name that repeats, and whether the file is previewed, unsaved or gone", () => {
  const files = [{ path: "src/index.ts", preview: false }, { path: "lib/index.ts", preview: true }, { path: "docs/<b>&notes.md", preview: false }];
  for (const locale of locales) {
    const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
      createElement(EditorTabs, { files, active: "lib/index.ts", state: path => ({ dirty: path === "src/index.ts", missing: path.startsWith("docs/") }),
        onSelect: never, onClose: never, onPin: never, onMove: never, onMenu: never })));
    const tabs = html.split('role="tab"').slice(1);
    assert.equal(tabs.length, 3);
    assert.ok(tabs[0].includes('aria-selected="false"') && tabs[0].includes('data-dirty="true"') && tabs[0].includes('data-preview="false"'), tabs[0]);
    assert.ok(tabs[0].includes('<span class="editor-tab-name">index.ts</span><span class="editor-tab-folder">src</span>'), tabs[0]);
    assert.ok(tabs[1].includes('aria-selected="true"') && tabs[1].includes('data-preview="true"') && tabs[1].includes('<span class="editor-tab-folder">lib</span>'), tabs[1]);
    // A path is text wherever it is written; a name alone has no folder beside it.
    assert.ok(tabs[2].includes('title="docs/&lt;b&gt;&amp;notes.md"') && tabs[2].includes(">&lt;b&gt;&amp;notes.md</span>") && !tabs[2].includes("editor-tab-folder"), tabs[2]);
    assert.ok(tabs[2].includes('data-missing="true"'));
    assert.ok(tabs[0].includes(translate(locale, "Unsaved changes")) && tabs[1].includes(translate(locale, "Close {name}", { name: "index.ts" })), html);
    assert.ok(html.includes(translate(locale, "Open files")));
  }
});

test("the question before a deletion says where the entry goes, and what is lost with it", () => {
  const file = { directory: false, permanent: false, trashFailed: false, unsaved: 0, platform: "windows" };
  assert.deepEqual(deletionWording(file), { failed: null, question: "Move {name} to the Recycle Bin?", unsaved: null, action: "Move to Recycle Bin" });
  assert.deepEqual(deletionWording({ ...file, directory: true, platform: "macos", unsaved: 1 }), { failed: null,
    question: "Move the folder {name} and everything in it to the Trash?", unsaved: "The unsaved changes of 1 open file will be lost.", action: "Move to Trash" });
  assert.deepEqual(deletionWording({ ...file, platform: "linux", unsaved: 3 }), { failed: null, question: "Move {name} to the Trash?",
    unsaved: "The unsaved changes of {count} open files will be lost.", action: "Move to Trash" });
  // The trash did not take it: the question is now the one of a deletion for good, and says why it is asked.
  assert.deepEqual(deletionWording({ ...file, permanent: true, trashFailed: true }), { failed: "It could not be moved to the Recycle Bin.",
    question: "Delete {name} permanently? This cannot be undone.", unsaved: null, action: "Delete permanently" });
  assert.deepEqual(deletionWording({ ...file, directory: true, permanent: true, trashFailed: true, platform: "linux" }), { failed: "It could not be moved to the Trash.",
    question: "Delete the folder {name} and everything in it permanently? This cannot be undone.", unsaved: null, action: "Delete permanently" });
  for (const locale of locales) for (const directory of [false, true]) for (const permanent of [false, true]) for (const platform of ["windows", "macos"]) {
    const wording = deletionWording({ directory, permanent, trashFailed: true, unsaved: 3, platform });
    const texts = [translate(locale, wording.failed!), translate(locale, wording.question, { name: "notes.md" }), translate(locale, wording.unsaved!, { count: 3 }), translate(locale, wording.action)];
    assert.ok(texts[1].includes("notes.md") && texts[2].includes("3") && texts.every(text => text.length > 0 && !/\{\w+\}/u.test(text)), `${locale}: ${texts.join(" | ")}`);
  }
});
