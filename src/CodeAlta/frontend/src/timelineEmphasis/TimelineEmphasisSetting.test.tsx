import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { TimelineEmphasisSetting } from "./TimelineEmphasisSetting";
import { TimelineEmphasisProvider } from "./useTimelineEmphasis";
import { defaultTimelineEmphasis, timelineEmphasisKey } from "./timelineEmphasis";

function rendered(stored: string | null): string {
  const previous = Object.getOwnPropertyDescriptor(globalThis, "localStorage");
  Object.defineProperty(globalThis, "localStorage", { configurable: true, value: { getItem: (key: string) => key === timelineEmphasisKey ? stored : null } });
  try { return renderToStaticMarkup(<TimelineEmphasisProvider><TimelineEmphasisSetting /></TimelineEmphasisProvider>); }
  finally { if (previous) Object.defineProperty(globalThis, "localStorage", previous); else delete (globalThis as { localStorage?: unknown }).localStorage; }
}

const customize = (markup: string) => markup.match(/<button[^>]*aria-haspopup="dialog"[^>]*>/)![0];

test("the row has the switch and a Customize button for the window of the types", () => {
  const markup = rendered(JSON.stringify({ ...defaultTimelineEmphasis, enabled: true }));
  assert.match(markup, /<input[^>]*id="settings-dim-intermediate"[^>]*checked/);
  assert.match(markup, />Customize</);
  assert.doesNotMatch(customize(markup), /disabled/);
  assert.match(customize(markup), /aria-expanded="false"/);
  // The types are set in the window, not on the page.
  assert.doesNotMatch(markup, /Your prompts|on hover/);
});

test("dimming starts off and Customize is disabled without a saved preference", () => {
  const markup = rendered(null);
  assert.doesNotMatch(markup.match(/<input[^>]*id="settings-dim-intermediate"[^>]*>/)![0], /checked/);
  assert.match(customize(markup), /disabled/);
});
