import test from "node:test";
import assert from "node:assert/strict";
import { renderToStaticMarkup } from "react-dom/server";
import { TimelineMessage, commandPreview } from "./TimelineMessage";
import { ProjectRailRows } from "./ProjectRailRows";
import type { TimelineItem } from "./timeline";
import { FileChangeInspection } from "./FileChangeInspection";

const record: TimelineItem = { key: "42", eventType: "sessionUpdate", category: "status", icon: "usage",
  title: "Idle", subtitle: null, timestamp: "2026-09-27T10:00:00Z", markdown: "Context: 48%\n\nInput tokens: 94449",
  summary: "Context: 48% · Input tokens: 94449", summaryIsCode: false, detailMarkdown: null, details: '{"literal":"<unsafe>"}', detailsLabel: "Details",
  metadata: ["Provider: literal"], truncated: false, bodyOmitted: false, copyMarkdown: "original\nbytes" };

test("command cards summarize identity without displaying entire command scripts", () => {
  assert.equal(commandPreview('git commit -m "long message"; git status --short'), "git commit…");
  assert.equal(commandPreview("dotnet test -c Release"), "dotnet test…");
  assert.ok(commandPreview('"' + "x".repeat(500)).length <= 49);
});

test("status rows are compact and details are dialog actions, not permanent disclosures", () => {
  const html = renderToStaticMarkup(<TimelineMessage item={record} />);
  assert.match(html, /timeline-compact/);
  assert.match(html, /aria-haspopup="dialog"/);
  assert.doesNotMatch(html, /<details|event-detail-body|Input tokens: 94449<\/p>/);
  assert.match(html, /Context: 48%/);
});

test("long assistant prose keeps its preview and raw actions have accessible labels", () => {
  // Short Markdown uses the DOMPurify browser boundary. Exercise the inert long-body
  // preview here without substituting a fake sanitizer or installing a DOM dependency.
  const html = renderToStaticMarkup(<TimelineMessage item={{ ...record, category: "assistant", markdown: "Hello **world** " + "text ".repeat(300) }} />);
  assert.match(html, /Hello \*\*world\*\*/);
  assert.match(html, /long-message-toggle/);
  assert.match(html, /aria-label="Details"/);
  assert.doesNotMatch(html, /<summary/);
});

test("selected project owns the nested session branch with one-line project chrome", () => {
  const html = renderToStaticMarkup(<ProjectRailRows projects={[{ id: "p", name: "Project", path: "C:/literal", archived: false }]}
    selectedId="p" onSelect={() => {}} canRename renameBusy={false} onRename={() => {}}><span>session-child</span></ProjectRailRows>);
  assert.match(html, /project-session-branch/);
  assert.match(html, /aria-expanded="true"/);
  assert.doesNotMatch(html, /Rename project \(F2\)|<small[^>]*>C:\/literal/);
  assert.match(html, /session-child/);
});

test("file rows preserve literal paths and hunk counts without inline diffs", () => {
  const html = renderToStaticMarkup(<FileChangeInspection changes={{ source: "supplied", partial: false,
    rows: [{ index: 0, path: "src/<literal>.ts", kind: "modify", diff: "private diff body", counts: { added: 3, removed: 1 } }] }} />);
  assert.match(html, /aria-haspopup="dialog"/);
  assert.match(html, /src\/&lt;literal&gt;\.ts/);
  assert.match(html, /\+3/);
  assert.match(html, /−1/);
  assert.doesNotMatch(html, /private diff body|<a |<pre/);
});
