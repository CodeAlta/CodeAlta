import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryResponse, SessionDisplayView } from "#neoastra";
import { reconcileTimeline, type ReconciledRow } from "../reconcileTimeline";
import { groupTimelineTools } from "../toolGroups";
import { defaultTimelineEmphasis, emphasisProperties, entryAppearance, groupEntryTypes, persistTimelineEmphasis, restoreTimelineEmphasis, rowEntryType } from "./timelineEmphasis";

type Entry = HistoryResponse["entries"][number];
const entry = (offset: string, overrides: Partial<Entry> = {}): Entry => ({ offset, eventType: "contentCompleted", providerId: "provider",
  sessionId: "session", runId: "run", timestamp: `2026-10-08T00:00:${offset.padStart(2, "0")}Z`, kind: "Assistant", phase: null,
  contentId: `content-${offset}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, prompt: null, name: null,
  tool: null, files: null, images: null, text: `message ${offset}`, details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...overrides });
const call = (offset: string, id: string): Entry => entry(offset, { eventType: "activity", kind: "ToolCall", activityId: id, contentId: null, name: "shell_command", phase: "Completed", text: null });

test("unapplied and lossy system prompts retain attention protection", () => {
  const row = reconcileTimeline([entry("1", { eventType: "system_prompt", kind: "session_start" })], null)[0];
  assert.equal(row.source, "history");
  if (row.source !== "history") return;
  const item = row.item;
  const prompt = { changed: false, name: "Default", sourcePath: null, hash: "preview", tokens: 100,
    systemTokens: 60, developerTokens: 40, applied: true, lossy: false, change: null };
  for (const flags of [{ applied: false, lossy: false }, { applied: true, lossy: true }]) {
    const row = { source: "history" as const, key: item.key, item: { ...item, prompt: { ...prompt, ...flags } } };
    assert.equal(rowEntryType(row), "attention");
    assert.equal(groupEntryTypes([{ key: row.key, tools: false, rows: [row] }]).get(row.key), "attention");
  }
  assert.equal(rowEntryType({ source: "history", key: item.key, item: { ...item, prompt } }), "systemPrompt");
});

test("fresh and reset defaults distinguish important entries, progress and background information without elevation", () => {
  const expected = { prompt: [100, true, true], agentMessage: [100, true, true], answer: [100, true, true], file: [100, true, true], plan: [100, true, true],
    step: [70, false, true], tool: [70, false, true], reasoning: [50, false, false], systemPrompt: [50, false, false], status: [50, false, false], plugin: [50, false, false] } as const;
  for (const type of Object.keys(expected) as (keyof typeof expected)[]) {
    const value = defaultTimelineEmphasis.types[type], paint = entryAppearance(value);
    assert.deepEqual([value.dim ? value.opacity : 100, paint.normal.outerBorder, paint.hover.outerBorder], expected[type], type);
    assert.equal(value.hoverOpacity, 100, type);
    assert.deepEqual([paint.normal.backgroundTint, paint.hover.backgroundTint], [8, 8], type);
    assert.deepEqual([paint.normal.elevation, paint.hover.elevation], ["none", "none"], type);
    assert.equal(paint.dimAccent, true, type);
    assert.equal(Object.isFrozen(paint.normal), true, type);
    assert.equal(Object.isFrozen(paint.hover), true, type);
  }
  for (const stored of ["{}", '{"enabled":true}', '{"types":{"tool":{},"reasoning":null}}']) {
    assert.deepEqual(restoreTimelineEmphasis(() => stored).types, defaultTimelineEmphasis.types, "missing types take the complete current preset");
  }
  assert.equal(JSON.stringify(restoreTimelineEmphasis(() => JSON.stringify(defaultTimelineEmphasis)).types), JSON.stringify(defaultTimelineEmphasis.types),
    "restored preset keeps the canonical order used to disable unchanged Reset controls");
});

test("appearance extends legacy preferences without rewriting their saved dim choices", () => {
  const restored = restoreTimelineEmphasis(() => JSON.stringify({ enabled: true, types: { tool: { dim: true, opacity: 35, hoverOpacity: 80 } } }));
  assert.deepEqual(restored.types.tool, { dim: true, opacity: 35, hoverOpacity: 80 });
  assert.deepEqual(entryAppearance(restored.types.tool), { normal: { backgroundTint: 8, outerBorder: true, elevation: "none" },
    hover: { backgroundTint: 8, outerBorder: true, elevation: "none" }, dimAccent: true });
  const background = emphasisProperties(restored)["--timeline-group-background-tool"];
  assert.equal(background, "color-mix(in srgb, var(--panel-2) 60%, transparent)");
  const appearance = entryAppearance(restored.types.tool);
  const borderOnly = { ...restored, types: { ...restored.types, tool: { ...restored.types.tool,
    appearance: { ...appearance, normal: { ...appearance.normal, outerBorder: false } } } } };
  assert.equal(emphasisProperties(borderOnly)["--timeline-group-background-tool"], background, "changing borders alone keeps the existing tool surface");
});

test("highlighting is independent of dimming and uses scheme roles, not stored colors", () => {
  const appearance = { normal: { backgroundTint: 8, outerBorder: false, elevation: "none" as const },
    hover: { backgroundTint: 14, outerBorder: true, elevation: "subtle" as const }, dimAccent: false };
  const value = { ...defaultTimelineEmphasis, enabled: true, types: { ...defaultTimelineEmphasis.types,
    prompt: { ...defaultTimelineEmphasis.types.prompt, appearance } } };
  const properties = emphasisProperties(value);
  assert.equal(properties["--timeline-opacity-prompt"], undefined, "no dimming required");
  assert.equal(properties["--timeline-tint-prompt"], "8%");
  assert.equal(properties["--timeline-hover-tint-prompt"], "14%");
  assert.equal(properties["--timeline-border-prompt"], "transparent");
  assert.equal(properties["--timeline-hover-border-prompt"], "var(--line)");
  assert.equal(properties["--timeline-hover-elevation-prompt"], "var(--timeline-subtle-shadow)");
  assert.equal(properties["--timeline-layer-prompt"], "auto");
  assert.equal(properties["--timeline-hover-layer-prompt"], "2");
  assert.equal(properties["--timeline-accent-opacity-prompt"], "1");
  assert.equal(Object.keys(properties).some(key => key.includes("attention")), false);
  assert.deepEqual(emphasisProperties({ ...value, enabled: false }), {});
});

test("appearance restoration validates each field and never writes automatically", () => {
  const restored = restoreTimelineEmphasis(() => JSON.stringify({ enabled: true, types: { tool: { dim: true, opacity: 60, hoverOpacity: 100,
    appearance: { normal: { backgroundTint: 300, outerBorder: "no", elevation: "huge" },
      hover: { backgroundTint: -20, outerBorder: false, elevation: "subtle" }, dimAccent: false } } } }));
  assert.deepEqual(entryAppearance(restored.types.tool), { normal: { backgroundTint: 30, outerBorder: true, elevation: "none" },
    hover: { backgroundTint: 0, outerBorder: false, elevation: "subtle" }, dimAccent: false });
  const roundTrip = restoreTimelineEmphasis(() => JSON.stringify(restored));
  assert.deepEqual(roundTrip, restored);
});

test("completed calls whose background work fails or stops protect their entire group", () => {
  const groups = groupTimelineTools(reconcileTimeline([call("1", "background"), call("2", "ordinary")], null), []);
  for (const state of ["failed", "stopped"] as const) {
    assert.equal(groupEntryTypes(groups, new Map([["background", state]])).get(groups[0].key), "attention");
  }
  assert.equal(groupEntryTypes(groups, new Map([["background", "running"]])).get(groups[0].key), "tool");
  const active = groupTimelineTools(reconcileTimeline([{ ...call("1", "background"), phase: "Started" }], null), []);
  assert.equal(groupEntryTypes(active, new Map([["background", "failed"]])).get(active[0].key), "tool", "a restarted call follows its current phase");
});
const update = (offset: string, kind: string): Entry => entry(offset, { eventType: "sessionUpdate", kind, contentId: null });
const view = (kind: string, text: string): SessionDisplayView => ({ sessionId: "session", revision: "1", lifecycle: null, queuedPromptCount: null,
  configuration: { providerId: "provider", providerKey: null, modelId: null, reasoningEffort: null, agentPromptId: null }, statusKind: null, statusMessage: null, conversationWait: null,
  text: [{ timestamp: "2026-10-08T00:01:00Z", sequence: "1", runId: "run", contentId: "streamed", kind, text, isComplete: false,
    isTruncated: false, startedWithDelta: true }], toolActivities: [], metadataTruncated: false,
  transportTruncated: false, evictedTextItems: "0", evictedToolActivities: "0", unsupportedEvents: "0" });

/** The title of each group of the timeline, and its type. */
function types(entries: Entry[], live: SessionDisplayView | null = null, extra: ReconciledRow[] = []) {
  const groups = groupTimelineTools([...reconcileTimeline(entries, live), ...extra], entries);
  const typed = groupEntryTypes(groups);
  return groups.map(group => {
    const row = group.rows[0];
    const title = group.tools ? "Tool calls" : row.source === "history" ? row.item.title : row.source === "liveText" ? `live ${row.row.kind}` : "live tool";
    return [title, typed.get(group.key)] as const;
  });
}

test("a turn has its prompt, its steps and its final answer, and each entry its type", () => {
  const entries = [
    entry("1", { kind: "User" }),
    entry("2", { kind: "Reasoning" }),
    entry("3", { eventType: "system_prompt", kind: "Initial", contentId: null, text: "You are an agent." }),
    update("4", "ModelChanged"),
    call("5", "a"), call("6", "b"),
    entry("7"),
    entry("8", { kind: "FileChangeOutput" }),
    entry("9", { kind: "Plan" }),
    entry("10"),
    entry("11", { kind: "User", text: "<agent_message from=\"parent\">Check this</agent_message>", sourceSessionId: "parent" }),
    entry("12"),
  ];
  const [answer] = reconcileTimeline([entry("13")], null);
  assert.equal(answer.source, "history");
  const plugin: ReconciledRow = { source: "history", key: "plugin:turn", item: { ...(answer as Extract<ReconciledRow, { source: "history" }>).item,
    key: "plugin:turn", eventType: "plugin", category: "plugin", title: "Turn statistics" } };
  assert.deepEqual(types(entries, null, [plugin]), [
    ["You", "prompt"], ["Reasoning", "reasoning"], ["System prompt changed", "systemPrompt"], ["Model changed", "status"], ["Tool calls", "tool"],
    // An answer the turn went on from is a step of it.
    ["Assistant", "step"], ["File changes", "file"], ["Plan", "plan"], ["Assistant", "answer"],
    // A message of another agent starts a turn of its own.
    ["Agent message", "agentMessage"], ["Assistant", "answer"], ["Turn statistics", "plugin"],
  ]);
});

test("what needs the user has a type of its own, which is never dimmed", () => {
  const entries = [
    entry("1", { kind: "User" }),
    entry("2", { eventType: "permissionRequest", kind: "CommandExecution", contentId: null }),
    entry("3", { eventType: "userInputRequest", kind: null, contentId: null }),
    entry("4", { eventType: "interaction", kind: "Answered", contentId: null }),
    entry("5", { eventType: "error", kind: null, contentId: null }),
    update("6", "Warning"),
  ];
  assert.deepEqual(types(entries).map(([, type]) => type), ["prompt", "attention", "attention", "attention", "attention", "attention"]);
  // A run of tool calls stands out when one of them failed.
  const failed = { ...call("8", "b"), phase: "Failed" };
  assert.deepEqual(types([entry("1", { kind: "User" }), call("2", "a"), call("3", "b")]).map(([, type]) => type), ["prompt", "tool"]);
  assert.deepEqual(types([entry("1", { kind: "User" }), call("2", "a"), failed]).map(([, type]) => type), ["prompt", "attention"]);
  assert.equal(rowEntryType({ source: "liveTool", key: "k", row: { providerId: "provider", runId: "run", activityId: "a", name: "shell_command", phase: "Failed", timestamp: null } } as unknown as ReconciledRow), "attention");
  const everything = { enabled: true, types: Object.fromEntries(Object.keys(defaultTimelineEmphasis.types)
    .map(type => [type, { dim: true, opacity: 10, hoverOpacity: 10 }])) } as typeof defaultTimelineEmphasis;
  assert.equal(Object.keys(emphasisProperties(everything)).some(name => name.endsWith("-attention")), false);
});

test("the newest answer of a running turn stays the answer until another one comes", () => {
  const entries = [entry("1", { kind: "User" }), entry("2"), call("3", "a")];
  assert.deepEqual(types(entries), [["You", "prompt"], ["Assistant", "answer"], ["Tool calls", "tool"]]);
  assert.deepEqual(types(entries, view("Assistant", "Done")), [["You", "prompt"], ["Assistant", "step"], ["Tool calls", "tool"], ["live Assistant", "answer"]]);
  assert.equal(rowEntryType({ source: "liveText", key: "k", row: view("Reasoning", "…").text[0] }), "reasoning");
  assert.equal(rowEntryType({ source: "liveText", key: "k", row: view("User", "Hi").text[0] }), "prompt");
  assert.equal(rowEntryType({ source: "liveText", key: "k", row: view("Plan", "Next steps").text[0] }), "plan");
  // A message of another agent is one already while it is live, as it is once it is in the history.
  const agent = { ...view("User", "[CodeAlta delegated-agent message]\nSource session: parent\nKind: note\n\nCheck this").text[0], isComplete: true };
  assert.equal(rowEntryType({ source: "liveText", key: "k", row: agent }), "agentMessage");
});

test("canceled tool calls stay prominent, including groups and live/history reconciliation", () => {
  const canceled = { ...call("3", "canceled"), phase: "Canceled" };
  assert.deepEqual(types([call("2", "completed"), canceled]).map(([, type]) => type), ["attention"]);
  const live = { ...view("Assistant", ""), text: [], toolActivities: [{ timestamp: null, sequence: null, providerId: "provider", runId: "run",
    activityId: "canceled", name: "shell_command", phase: "Canceled", isNameTruncated: false }] };
  assert.deepEqual(types([], live).map(([, type]) => type), ["attention"]);
  const rows = reconcileTimeline([{ ...canceled, phase: "Started" }], live);
  assert.equal(rows.length, 1);
  assert.equal(rows[0].source, "history");
  assert.equal(rowEntryType(rows[0]), "attention");
  for (const phase of ["Started", "Completed"]) {
    assert.deepEqual(types([{ ...canceled, phase }]).map(([, type]) => type), ["tool"]);
  }
});

test("dimming is off by default, while an explicit saved choice is preserved", () => {
  for (const stored of [null, "not json", "{}", '{"enabled":"yes"}']) {
    const value = restoreTimelineEmphasis(() => stored);
    assert.equal(value.enabled, false);
    assert.deepEqual(emphasisProperties(value), {});
  }
  assert.equal(restoreTimelineEmphasis(() => '{"enabled":true}').enabled, true);
  assert.equal(restoreTimelineEmphasis(() => '{"enabled":false}').enabled, false);
});

test("the timeline gives an opacity to the dimmed types only, and none when dimming is off", () => {
  const properties = emphasisProperties({ ...defaultTimelineEmphasis, enabled: true });
  assert.equal(properties["--timeline-opacity-tool"], "0.7");
  assert.equal(properties["--timeline-hover-opacity-tool"], "1");
  for (const type of ["prompt", "agentMessage", "answer", "file", "plan"]) assert.equal(properties[`--timeline-opacity-${type}`], undefined, type);
  const custom = { ...defaultTimelineEmphasis, enabled: true, types: { ...defaultTimelineEmphasis.types, reasoning: { dim: true, opacity: 35, hoverOpacity: 80 } } };
  assert.equal(emphasisProperties(custom)["--timeline-opacity-reasoning"], "0.35");
  assert.equal(emphasisProperties(custom)["--timeline-hover-opacity-reasoning"], "0.8");
  assert.deepEqual(emphasisProperties({ ...defaultTimelineEmphasis, enabled: false }), {});
});

test("failed, timed-out and canceled background jobs stay prominent and start their own turn, live and persisted", () => {
  for (const result of ["failed (exit code 1) after 12 s", "timed out and was stopped after 30 s", "stopped before it ended after 1 s"]) {
    const text = `[CodeAlta background job]\nJob: job-1234\nTitle: Verify\nCommand: dotnet test\nResult: ${result}\n\nOutput:\nerror`;
    const job = entry("3", { kind: "User", text });
    assert.deepEqual(types([entry("1", { kind: "User" }), entry("2"), job, entry("4")]).map(([, type]) => type),
      ["prompt", "answer", "attention", "answer"]);
    assert.equal(rowEntryType({ source: "liveText", key: "job", row: { ...view("User", text).text[0], isComplete: true } }), "attention");
    const live = { ...view("User", text), text: [{ ...view("User", text).text[0], isComplete: true, contentId: "job" },
      { ...view("Assistant", "Done").text[0], contentId: "answer", sequence: "2", timestamp: "2026-10-08T00:01:01Z" }] };
    assert.deepEqual(types([entry("1", { kind: "User" }), entry("2")], live).map(([, type]) => type),
      ["prompt", "answer", "attention", "answer"]);
  }
});

test("hover and keyboard focus never reduce opacity, without rewriting saved choices", () => {
  for (const [opacity, hoverOpacity] of [[80, 35], [50, 50], [100, 10]]) {
    const stored = JSON.stringify({ ...defaultTimelineEmphasis, enabled: true, types: { ...defaultTimelineEmphasis.types,
      reasoning: { dim: true, opacity, hoverOpacity } } });
    const value = restoreTimelineEmphasis(() => stored);
    assert.deepEqual(value.types.reasoning, { dim: true, opacity, hoverOpacity }, "restoration preserves the user's choices");
    assert.equal(emphasisProperties(value)["--timeline-hover-opacity-reasoning"], String(Math.max(opacity, hoverOpacity) / 100));
    assert.equal(emphasisProperties(value)["--timeline-opacity-reasoning"], String(opacity / 100));
    let kept = "";
    assert.equal(persistTimelineEmphasis(text => { kept = text; }, value), true);
    assert.deepEqual(JSON.parse(kept).types.reasoning, { dim: true, opacity, hoverOpacity });
  }
});

test("the setting is kept as JSON, and what is missing or invalid takes its default", () => {
  assert.equal(Object.isFrozen(defaultTimelineEmphasis), true);
  assert.equal(Object.isFrozen(defaultTimelineEmphasis.types), true);
  for (const value of Object.values(defaultTimelineEmphasis.types)) assert.equal(Object.isFrozen(value), true);
  assert.deepEqual(restoreTimelineEmphasis(() => null), defaultTimelineEmphasis);
  assert.deepEqual(restoreTimelineEmphasis(() => "not json"), defaultTimelineEmphasis);
  assert.deepEqual(restoreTimelineEmphasis(() => { throw new Error("unavailable"); }), defaultTimelineEmphasis);
  const restored = restoreTimelineEmphasis(() => JSON.stringify({ enabled: false, types: { tool: { dim: false, opacity: 33, hoverOpacity: 400 },
    reasoning: { dim: "yes", opacity: -5 }, unknown: { dim: true } } }));
  assert.equal(restored.enabled, false);
  // Opacities are whole steps of 5% from 10% to 100%.
  assert.deepEqual(restored.types.tool, { dim: false, opacity: 35, hoverOpacity: 100 });
  assert.deepEqual(restored.types.reasoning, { dim: true, opacity: 10, hoverOpacity: 100 });
  assert.deepEqual(restored.types.prompt, defaultTimelineEmphasis.types.prompt);
  assert.equal("unknown" in restored.types, false);
  let kept: string | null = null;
  assert.equal(persistTimelineEmphasis(value => { kept = value; }, restored), true);
  assert.deepEqual(restoreTimelineEmphasis(() => kept), restored);
  assert.equal(persistTimelineEmphasis(() => { throw new Error("full"); }, restored), false);
});
