import test from "node:test";
import assert from "node:assert/strict";
import { historyMessage } from "./history";
import { timelineNotice } from "./timelineScroll";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import { messages, locales, translate, resolveLocale, readLanguage, saveLanguage } from "./localization";
import { inventoryNotice } from "./inventoryNotice";
import { workflowNotice } from "./workflowNotice";
import { ProjectRailRows } from "./ProjectRailRows";
import { ShellLanguageContext } from "./shellLanguage";
import { AboutDialog } from "./AboutDialog";

test("support labels translate while recorded host identity and confirmation bytes stay literal", () => {
  for (const locale of locales) {
    for (const key of ["About CodeAlta", "Refresh logs", "Clear captured messages…", "Check reference metadata", "Close references", "Path metadata observed; Send revalidates", "Raw prompt reference preview"] as const) {
      if (locale !== "en") assert.notEqual(translate(locale, key), key);
    }
    assert.ok(translate(locale, "Type {confirmation} to confirm", { confirmation: "CLEAR CAPTURED LOGS" }).includes("CLEAR CAPTURED LOGS"));
    const markup = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: () => {} } },
      createElement(AboutDialog, { demo: false, bootError: false, onClose: () => assert.fail("render cannot close"),
        status: { productName: "Settings", version: "1.2.3+Copy", state: "owned-text-only", hostAvailable: true, hostEpoch: "Close",
          commandReviewEnabled: false, ownedAsksEnabled: false, ownedUserInputEnabled: false } })));
    assert.ok(markup.includes("<dd>Settings</dd>") && markup.includes("<dd>1.2.3+Copy</dd>") && markup.includes("<dd>Copy</dd>"));
  }
});

test("project/session workflow labels translate while literal titles, paths and controller feedback do not", () => {
  const literal = "Settings Unknown Archived C:/Settings/<literal>";
  for (const locale of locales) {
    for (const key of ["Project details", "Copy project path", "Rename project (F2)", "Confirm archive", "Confirm unarchive",
      "Find a saved project by name or full path", "Suggest folders", "Import and open folder", "Create and open", "Save title", "Save project name"] as const) {
      if (locale !== "en") assert.notEqual(translate(locale, key), key);
    }
    assert.equal(workflowNotice(locale, literal), literal);
    assert.ok(workflowNotice(locale, { key: "Rename project {name}", parameters: { name: literal } }).includes(literal));
    const markup = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: () => {} } },
      createElement(ProjectRailRows, { projects: [{ id: "Settings", name: "Archived", path: "C:/Settings/Unknown", archived: true }],
        selectedId: "Settings", onSelect: () => assert.fail("render cannot navigate"), canRename: true, renameBusy: false, onRename: () => assert.fail("render cannot rename") })));
    assert.ok(markup.includes("<strong>Archived</strong>"));
    assert.ok(markup.includes("C:/Settings/Unknown"));
    assert.ok(markup.includes(translate(locale, "Archived")));
  }
});

test("inventory labels translate in six languages while status codes and English-like values stay literal", () => {
  for (const locale of locales) {
    for (const key of ["Provider management", "Model catalog", "Agent prompts", "MCP Servers", "Configured providers",
      "Search models", "Adapter type", "Cached availability",
      "Inspect supplied tool record", "Supplied record provenance", "Copy supplied tool JSON", "Display excerpt; Copy retains the supplied JSON.",
      "Retained original Send prompt", "Use model for next Send"] as const) {
      assert.ok(translate(locale, key));
      if (locale !== "en") assert.notEqual(translate(locale, key), key);
    }
    for (const literal of ["Settings", "Unknown", "Ready", "Disabled", "project_read_error", "C:\\Settings\\Models", "<tools> --help"]) {
      assert.ok(translate(locale, "Details for {name}", { name: literal }).includes(literal));
      assert.ok(inventoryNotice(locale, { key: "Model inventory unavailable ({status}).", status: literal }).includes(literal));
    }
    assert.equal(inventoryNotice(locale, ""), "");
    assert.equal(inventoryNotice(locale, "Host identity changed. Reload required."), translate(locale, "Host identity changed. Reload required."));
  }
});

test("provider and timeline UI labels translate without translating literal content or decision tokens", () => {
  const literal = "  Settings Copy failed Allow once <script>日本語</script>  ";
  for (const locale of locales) {
    for (const key of ["Nonsecret provider input", "Review provider input", "Local input draft discarded. No answer or cancellation was sent by dismissal.", "Pending command permissions", "Refresh pending commands", "Submit literal answers", "Session timeline", "Refresh newest history", "Jump to latest visible", "Show full message", "Copy failed"] as const) {
      assert.ok(translate(locale, key));
      if (locale !== "en") assert.notEqual(translate(locale, key), key);
    }
    assert.ok(translate(locale, "Copy {title} as Markdown", { title: literal }).includes(literal));
    assert.ok(translate(locale, "Original {action} for {session}: {status}. No automatic replay.", { action: "resolve", session: literal, status: "rejected" }).includes(literal));
    assert.ok(timelineNotice(locale, { key: "Retained message: {label}", parameters: { label: literal } }).includes(literal));
    assert.ok(timelineNotice(locale, { key: "Newest history refresh failed: {error} Follow preference unchanged.", error: "history_changed" }).includes(translate(locale, historyMessage("history_changed"))));
    for (const code of ["unconfigured", "missing_session", "outside_root", "invalid_cursor", "history_changed", "unsupported_format", "record_too_large", "corrupt_record", "wire_limit", "Settings"]) {
      assert.ok(translate(locale, historyMessage(code)));
    }
    assert.equal(timelineNotice(locale, null), "");
  }
});

test("interaction controls translate in every locale without changing literal parameters", () => {
  for (const locale of locales.filter(value => value !== "en")) {
    for (const key of ["Send", "Message draft", "Edit prompt", "Search commands", "Reopen closed tab"] as const)
      assert.notEqual(translate(locale, key), key);
    const literal = "<user title & /file.ts>";
    assert.ok(translate(locale, "Close tab: {label}", { label: literal }).includes(literal));
    assert.ok(renderToStaticMarkup(createElement("button", null, translate(locale, "Remove {title}", { title: literal }))).includes("&lt;user title &amp; /file.ts&gt;"));
  }
  assert.equal(translate("es", "Send"), "Enviar");
  assert.equal(translate("ja", "Edit prompt"), "プロンプトを編集");
});

test("all six shell dictionaries are complete immutable plain-text messages with matching parameters", () => {
  for (const [key, translations] of Object.entries(messages)) {
    assert.equal(translations.length, 5); assert.ok(Object.isFrozen(translations));
    for (const locale of locales) {
      const value = translate(locale, key as keyof typeof messages);
      assert.ok(value.length > 0);
      assert.deepEqual(value.match(/\{\w+\}/g)?.sort() ?? [], key.match(/\{\w+\}/g)?.sort() ?? [], `${locale}: ${key}`);
    }
  }
  const text = translate("es", "{count} sessions", { count: "<img src=x onerror=alert(1)>" });
  assert.ok(renderToStaticMarkup(createElement("p", null, text)).includes("&lt;img"));
  assert.equal(translate("unknown", "Settings"), "Settings");
});

test("management labels translate without translating confirmation bytes", () => {
  for (const locale of locales) {
    for (const key of ["Keep draft", "Batch session deletion", "Select visible eligible", "Confirm exact batch deletion", "Stop after pending original", "uncertain"] as const) {
      if (locale !== "en") assert.notEqual(translate(locale, key), key, `${locale}: ${key}`);
      else assert.equal(translate(locale, key), key);
    }
    for (const count of [1, 3, 32]) {
      const confirmation = `DELETE ${count}`;
      assert.ok(translate(locale, "Type {confirmation} to permanently delete exactly these reviewed session histories", { confirmation }).includes(confirmation));
    }
  }
});

test("canonical bounded locale choice, deterministic browser auto and SSR fallback", () => {
  assert.equal(resolveLocale("auto", ["xx", "es-MX"]), "es");
  assert.equal(resolveLocale("auto", ["ja-JP"]), "ja");
  assert.equal(resolveLocale("auto", ["zh-CN"]), "zh-CN");
  assert.equal(resolveLocale("auto", []), "en");
  assert.equal(resolveLocale("ES", ["ja"]), "en");
  assert.equal(resolveLocale("unknown", ["ja"]), "en");
  assert.equal(resolveLocale("auto", Array(8).fill("xx").concat("es")), "en");
});

test("reminder and caller-ask labels preserve literal payloads, identities and invariant duration help", () => {
  for (const locale of locales) {
    for (const key of ["Refresh reminders", "Create reminder", "Save message", "Uncertain reminder Save", "Delete confirmed reminder",
      "Check asks", "Answer original ask", "Cancel original ask", "Ask question navigation", "Captured original ask answer", "Confirm discard local draft"] as const) {
      assert.ok(translate(locale, key));
      if (locale !== "en") assert.notEqual(translate(locale, key), key);
    }
    const literal = "  Answer <Settings> 日本語 {index}  ";
    assert.ok(translate(locale, "Unsubmitted answer for {title}", { title: literal }).includes(literal));
    assert.ok(translate(locale, "Reminder list unavailable ({status}).", { status: literal }).includes(literal));
    assert.ok(translate(locale, "Question {index} of {count}: {title}", { index: 1, count: 2, title: literal }).includes(literal));
    assert.ok(translate(locale, "Delay: whole seconds (1–86400) or invariant HH:mm:ss / d.HH:mm:ss").includes("HH:mm:ss / d.HH:mm:ss"));
  }
});

test("storage failures and malformed values never cause automatic writes; failed saves apply locally", () => {
  assert.deepEqual(readLanguage(() => "es"), { choice: "es" });
  assert.deepEqual(readLanguage(() => null), { choice: "auto" });
  for (const value of ["ES", "es-MX", "", "null", "\"fr\"", "x".repeat(10000)])
    assert.deepEqual(readLanguage(() => value), { choice: "en", issue: "invalid" });
  assert.deepEqual(readLanguage(() => { throw new Error("denied"); }), { choice: "en", issue: "unavailable" });
  assert.deepEqual(saveLanguage("ja", () => { throw new Error("denied"); }), { choice: "ja", issue: "unsaved" });
  let writes = 0; assert.equal(saveLanguage("bad", () => { writes++; }), null); assert.equal(writes, 0);
});
