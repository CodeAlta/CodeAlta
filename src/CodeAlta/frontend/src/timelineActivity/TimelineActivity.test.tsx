import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { HistoryResponse } from "#neoastra";
import { reconcileTimeline } from "../reconcileTimeline";
import { groupTimelineTools } from "../toolGroups";
import { groupEntryTypes } from "../timelineEmphasis/timelineEmphasis";
import { foldTimeline } from "./activityFold";
import { TimelineActivity } from "./TimelineActivity";

type Entry = HistoryResponse["entries"][number];
const entry = (offset: string, overrides: Partial<Entry> = {}): Entry => ({ offset, eventType: "contentCompleted", providerId: "provider",
  sessionId: "session", runId: "run", timestamp: `2026-10-16T00:00:${offset.padStart(2, "0")}Z`, kind: "Assistant", phase: null,
  contentId: `content-${offset}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, prompt: null, name: null,
  tool: null, files: null, images: null, text: `message ${offset}`, details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...overrides });
const call = (offset: string, id: string, phase: string, command: string) => entry(offset, { eventType: "activity", kind: "ToolCall", activityId: id,
  contentId: null, name: "Bash", phase, text: null, tool: { primary: command, isCommand: true, output: null, outputLines: 0, outputBytes: null, exitCode: null, added: null, removed: null, fields: [] } as unknown as Entry["tool"] });

function render(entries: Entry[], expanded: boolean, live: boolean, images = false): string {
  const rows = reconcileTimeline(entries, null);
  const groups = groupTimelineTools(rows, entries);
  const types = groupEntryTypes(groups);
  const block = foldTimeline(groups, types).find(block => block.kind === "activity");
  if (block?.kind !== "activity") throw new Error("an activity");
  return renderToStaticMarkup(<TimelineActivity sessionId="session" groups={block.groups} entryTypes={types} expanded={expanded} live={live} images={images} onToggle={() => {}}
    revision={null} sources={new Map()} echoImages={new Map()} canInspect={() => true} onOpenSource={() => {}} onOpenTool={() => {}}
    readImages={offset => ({ key: `read:${offset}`, peek: index => ({ status: "ready", url: `data:image/png;base64,${offset}${index}` }), load: async () => ({ status: "failed" }) })} />);
}

const work = [entry("1", { kind: "User" }), entry("2", { kind: "Reasoning", text: "**Reading the release log**\n\nThe push ended." }),
  call("3", "a", "Completed", "gh run view 42"), call("4", "b", "Failed", "npm test"), call("5", "c", "Started", "wc -l run.log")];

test("a closed activity is one line: the thought, the counts and the time, without its rows", () => {
  const html = render(work, false, false);
  assert.match(html, /aria-expanded="false"/);
  assert.match(html, /Reading the release log/);
  assert.match(html, /data-state="failed"/, "a failed call marks the line");
  assert.doesNotMatch(html, /timeline-activity-steps|timeline-activity-body|tool-tile-title/);
  assert.doesNotMatch(html, /timeline-activity-succeeded|timeline-activity-running/, "an activity that is over keeps only what failed");
  assert.match(html, /timeline-activity-failed/);
  assert.match(html, /3s</, "the time from its first record to its last");
});

test("a live activity moves, counts its calls and shows its last steps", () => {
  const html = render(work, false, true);
  assert.match(html, /data-state="running"/);
  assert.match(html, /timeline-activity-succeeded[^>]*>.*?1</);
  assert.match(html, /timeline-activity-running/);
  assert.match(html, /<ol class="timeline-activity-steps"/);
  assert.match(html, /gh run view 42/);
  assert.match(html, /wc -l run\.log/);
});

test("without a thought the line says that tools are called, and which", () => {
  const html = render([entry("1", { kind: "User" }), call("2", "a", "Started", "wc -l x"), call("3", "b", "Requested", "C:\\tools\\rg.exe foo")], false, true);
  assert.match(html, /Calling tools…<small>Bash wc, Bash rg\.exe<\/small>/);
});


// A screenshot a tool gave the model: the output of its call carries the image.
const shot = (offset: string, id: string, title: string) => [call(offset, id, "Completed", `snapshot ${id}`),
  entry(`${offset}1`, { kind: "ToolOutput", parentActivityId: id, contentId: null, text: null, images: [{ index: 0, title, mediaType: "image/png" }] as unknown as Entry["images"],
    tool: { primary: null, isCommand: false, output: null, outputLines: 0, outputBytes: null, exitCode: null, added: null, removed: null, fields: [] } as unknown as Entry["tool"] })];

test("a folded line shows the images the model read in one strip; opened, the rows show them where they were read", () => {
  const read = [entry("1", { kind: "User" }), ...shot("2", "a", "first.png"), ...shot("3", "b", "second.png")];
  const folded = render(read, false, false, true);
  assert.match(folded, /class="timeline-activity-images" role="group" aria-label="2 image\(s\) read"/);
  assert.deepEqual([...folded.matchAll(/title="([^"]+\.png)"/g)].map(match => match[1]), ["first.png", "second.png"], "in the order they were read");
  assert.match(folded, /data:image\/png;base64,21/, "each image is read from its own row");
  assert.doesNotMatch(folded, /timeline-activity-body/);
  assert.doesNotMatch(render(read, true, false, true), /timeline-activity-images/, "the open line shows the timeline as it is");
  assert.doesNotMatch(render(read, false, false, false), /timeline-activity-images/, "the setting turns the strip off");
  assert.match(render(read, false, true, true), /timeline-activity-steps[\s\S]*timeline-activity-images/, "while the work goes on, under its last steps");
  assert.doesNotMatch(render(work, false, false, true), /timeline-activity-images/, "work that read no image has no strip");
});

test("an image read from a file is named by that file in the strip", () => {
  const [, output] = shot("2", "a", "20261021-tool-image.png");
  const read = [entry("1", { kind: "User" }), call("2", "a", "Completed", "C:\\work\\img\\diagram.png"), output, ...shot("3", "b", "screen.png")];
  const titles = [...render(read, false, false, true).matchAll(/title="([^"]+\.png)"/g)].map(match => match[1]);
  assert.deepEqual(titles, ["diagram.png", "screen.png"], "a call that names no file keeps the name of the image");
});
