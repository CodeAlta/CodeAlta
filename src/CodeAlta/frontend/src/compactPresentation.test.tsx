import test from "node:test";
import assert from "node:assert/strict";
import { renderToStaticMarkup } from "react-dom/server";
import { TimelineMessage, commandPreview } from "./TimelineMessage";
import { ProjectRailRows } from "./explorer/ProjectRailRows";
import type { TimelineItem } from "./timeline";
import { FileChangeInspection } from "./FileChangeInspection";
import { parseJobResult } from "./jobResult";
import { AutomationOriginContext } from "./automations/AutomationOrigin";

const record: TimelineItem = { key: "42", eventType: "sessionUpdate", category: "status", icon: "usage",
  title: "Idle", subtitle: null, timestamp: "2026-09-27T10:00:00Z", markdown: "Context: 48%\n\nInput tokens: 94449",
  summary: "Context: 48% · Input tokens: 94449", summaryIsCode: false, detailMarkdown: null, details: '{"literal":"<unsafe>"}', detailsLabel: "Details",
  metadata: ["Provider: literal"], truncated: false, bodyOmitted: false, copyMarkdown: "original\nbytes" };

test("command cards summarize identity without displaying entire command scripts", () => {
  assert.equal(commandPreview('git commit -m "long message"; git status --short'), 'git commit -m "long message"; git status --short');
  assert.equal(commandPreview("dotnet test -c Release"), "dotnet test -c Release");
  assert.ok(commandPreview('"' + "x".repeat(500)).length <= 161);
});

test("tool cards keep output statistics separate from clipped previews and expose outcomes", () => {
  for (const title of ["shell_command", "alta", "apply_patch"]) {
    const html = renderToStaticMarkup(<TimelineMessage toolTile item={{ ...record, category: "tool", title,
      markdown: null, summary: "command with long arguments", summaryIsCode: true, toolPhase: "completed",
      toolOutput: "output ".repeat(100), toolOutputLines: 27, toolOutputBytes: 1434 }} />);
    assert.match(html, /tool-output-stats[^>]*>27L · 1\.4 KB/);
    assert.match(html, /tool-outcome[^>]*>Completed/);
    assert.match(html, new RegExp(title));
  }
  const unknown = renderToStaticMarkup(<TimelineMessage toolTile item={{ ...record, category: "tool", markdown: null,
    toolPhase: "failed", toolOutput: "incomplete preview" }} />);
  assert.match(unknown, /tool-outcome[^>]*>Failed/);
  assert.doesNotMatch(unknown, /tool-output-stats/);
});

test("background-job results are compact clickable cards, not inline command and output blocks", () => {
  const job = parseJobResult("[CodeAlta background job]\nJob: job-1234\nTitle: Verify the build\nCommand: dotnet test " + "--filter Long ".repeat(30)
    + "\nResult: succeeded (exit code 0) after 10 min 45 s\n\nOutput:\n```\nprivate output body\n```")!;
  const html = renderToStaticMarkup(<TimelineMessage item={{ ...record, category: "user", title: "Background job", icon: "terminal",
    jobResult: job, markdown: job.body, summary: job.title, subtitle: job.result }} />);
  assert.match(html, /message-job timeline-compact/);
  assert.match(html, /tool-tile-title[^>]*aria-haspopup="dialog"/);
  assert.match(html, /Verify the build/);
  assert.match(html, /succeeded \(exit code 0\) after 10 min 45 s/);
  assert.doesNotMatch(html, /private output body|<pre|markdown-content|long-message-toggle/);
});

test("the prompt an automation sent names that automation on its card, and no other prompt does", () => {
  const origin = { name: "Nightly review", summary: "Issue · #12 Crash", onOpen: () => assert.fail("rendering must not act") };
  const prompt = (provenance: string | undefined) => renderToStaticMarkup(<AutomationOriginContext.Provider value={origin}>
    <TimelineMessage item={{ ...record, category: "user", title: "Automation", icon: "automation", markdown: null, summary: null, provenance }} /></AutomationOriginContext.Provider>);
  assert.match(prompt("automation"), /<strong>Automation<\/strong>[\s\S]*class="automation-origin"><button[^>]*>Nightly review<\/button> · Issue · #12 Crash/);
  assert.doesNotMatch(prompt("reminder"), /automation-origin/);
  assert.doesNotMatch(prompt(undefined), /automation-origin/);
});

test("status rows are compact and details are dialog actions, not permanent disclosures", () => {
  const html = renderToStaticMarkup(<TimelineMessage item={record} />);
  assert.match(html, /timeline-compact/);
  assert.match(html, /aria-haspopup="dialog"/);
  assert.doesNotMatch(html, /<details|event-detail-body|Input tokens: 94449<\/p>/);
  assert.match(html, /Context: 48%/);
});

test("long supporting diagnostics retain optional previews and accessible raw actions", () => {
  // Always-visible conversation Markdown is covered at the real sanitizer/browser
  // boundary by messageContent.browser.test.ts, not a fake Node DOM.
  const html = renderToStaticMarkup(<TimelineMessage item={{ ...record, category: "error", markdown: "Hello **world** " + "text ".repeat(300) }} />);
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
