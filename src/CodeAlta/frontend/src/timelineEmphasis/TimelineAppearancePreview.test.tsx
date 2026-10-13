import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { BackgroundCallsContext } from "../BackgroundTaskViews";
import { TimelineBookmarksContext, type TimelineBookmarks } from "../bookmarks/timelineBookmarks";
import { SystemPromptReaderContext } from "../SystemPromptDialog";
import type { SystemPromptReader } from "../systemPromptReader";
import { MarkdownLinksContext } from "../MarkdownContent";
import { PluginPaneContext, PluginUiContext, noPluginContributions } from "../pluginUi";
import { SessionLinksContext } from "../SessionReference";
import { rowEntryType, defaultTimelineEmphasis, emphasisProperties, timelineEntryTypes } from "./timelineEmphasis";
import { TimelineAppearancePreview, timelineAppearanceSample } from "./TimelineAppearancePreview";

const never = () => assert.fail("preview must not invoke a host action");
const identity = (key: string) => key;
// MarkdownContent is browser-only. Its five samples are rendered in the mounted browser regression, not mocked here.
const compactTypes = ["reasoning", "tool", "file", "systemPrompt", "status", "plugin"] as const;

test("all eleven preview samples are synthetic, safe and correctly classified", () => {
  assert.equal(timelineEntryTypes.length, 11);
  for (const type of timelineEntryTypes) {
    const item = timelineAppearanceSample(type, identity);
    assert.equal(rowEntryType({ source: "history", key: item.key, item }), type === "step" ? "answer" : type);
    assert.equal(item.sourceSessionId, undefined);
    assert.equal(item.script, undefined);
    assert.equal(item.scriptProblem, undefined);
    assert.equal(item.pluginKey, undefined);
    assert.equal(item.html, undefined);
    assert.equal(item.images, undefined);
    assert.equal(item.toolCall, undefined);
    assert.equal(item.copyMarkdown, null);
    assert.equal(item.details, null);
    if (["prompt", "agentMessage", "answer", "step", "plan"].includes(type)) assert.ok(item.markdown);
    else assert.equal(item.markdown, null);
    assert.deepEqual(item.metadata, type === "tool" ? ["Completed"] : []);
  }
});

test("compact previews render the same production group twice with forced states and disabled inspection", () => {
  for (const type of compactTypes) {
    const html = renderToStaticMarkup(<TimelineAppearancePreview type={type} emphasis={defaultTimelineEmphasis} />);
    assert.match(html, /class="timeline-appearance-preview-columns"/);
    assert.match(html, /<h4>Normal<\/h4>/);
    assert.match(html, /<h4>Hover \/ keyboard focus<\/h4>/);
    assert.equal((html.match(/data-preview-state="normal"/g) ?? []).length, 1);
    assert.equal((html.match(/data-preview-state="hover"/g) ?? []).length, 1);
    assert.equal((html.match(/class="messages timeline-appearance-enabled"/g) ?? []).length, 2);
    assert.equal((html.match(/ inert=""/g) ?? []).length, 2);
    assert.equal((html.match(new RegExp(`data-entry-type="${type}"`, "g")) ?? []).length, 2);
    assert.equal((html.match(/<article class="message timeline-message/g) ?? []).length, type === "tool" ? 4 : 2);
    assert.doesNotMatch(html, /data-alta-command|data-alta-action|<iframe|<script|<img|copy-markdown/);
    if (type === "tool") {
      assert.equal((html.match(/class="timeline-tool-group"/g) ?? []).length, 2);
      assert.match(html, /data-phase="completed"/);
      assert.match(html, /12L/);
      assert.equal((html.match(/class="tool-tile-title"/g) ?? []).length, 4);
    }
    if (type === "file") {
      assert.equal((html.match(/data-file-record="0"/g) ?? []).length, 2);
      assert.match(html, /data-file-record="0"[^>]*disabled/);
    }
    if (type === "systemPrompt") {
      assert.equal((html.match(/class="tool-tile-title"/g) ?? []).length, 2);
      assert.match(html, /System prompt recorded/);
      assert.match(html, /Default.*1.2k tokens/);
      assert.doesNotMatch(html, /Follow the project instructions/);
    }
  }
});

test("draft appearance is forced on and does not mutate the supplied preference", () => {
  const before = JSON.stringify(defaultTimelineEmphasis);
  const properties = emphasisProperties({ ...defaultTimelineEmphasis, enabled: true });
  const html = renderToStaticMarkup(<TimelineAppearancePreview type="tool" emphasis={defaultTimelineEmphasis} />);
  for (const [key, value] of Object.entries(properties)) assert.ok(html.includes(`${key}:${value}`), key);
  assert.equal(JSON.stringify(defaultTimelineEmphasis), before);
});

test("inherited host contexts cannot participate in rendering any preview", () => {
  for (const type of compactTypes) {
    const html = renderToStaticMarkup(<TimelineBookmarksContext.Provider value={{ subscribe: never, bookmarkOf: never } as unknown as TimelineBookmarks}>
      <SystemPromptReaderContext.Provider value={{ read: never } as unknown as SystemPromptReader}>
      <MarkdownLinksContext.Provider value={never}>
      <SessionLinksContext.Provider value={{ title: never, open: never }}>
        <PluginPaneContext.Provider value={{ sessionId: "real-session", projectId: "real-project" }}>
          <PluginUiContext.Provider value={{ epoch: "real", projectId: "real-project", contributions: noPluginContributions, run: never, runNamed: never }}>
            <BackgroundCallsContext.Provider value={{ get: never } as unknown as ReadonlyMap<string, never>}>
              <TimelineAppearancePreview type={type} emphasis={defaultTimelineEmphasis} />
            </BackgroundCallsContext.Provider>
          </PluginUiContext.Provider>
        </PluginPaneContext.Provider>
      </SessionLinksContext.Provider>
    </MarkdownLinksContext.Provider>
    </SystemPromptReaderContext.Provider></TimelineBookmarksContext.Provider>);
    assert.doesNotMatch(html, /real-session|real-project/);
  }
});
