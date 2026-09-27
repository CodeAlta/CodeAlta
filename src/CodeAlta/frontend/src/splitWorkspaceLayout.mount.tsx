import { StrictMode, useEffect, useRef } from "react";
import { createRoot } from "react-dom/client";
import { Node } from "flexlayout-react";
import { SplitWorkspaceLayout } from "./SplitWorkspaceLayout";
import { WorkspaceLayout } from "./WorkspaceLayout";
import { SessionContentLayout } from "./SessionContentLayout";
import "./style.css";

// Isolated fake caller; no App, host bridge or production audit API. Ordinary root.render
// scheduling is intentional, including refresh and fresh-instance mounting.
const container = document.getElementById("app")!;
const nativeFrame = window.requestAnimationFrame.bind(window);
const nativeCancel = window.cancelAnimationFrame.bind(window);
type Source = "key" | "resize" | "replay";
function sourceAudit() { return { calls: 0, scheduled: 0, fired: 0, canceled: 0, pending: 0 }; }
function newAudit() {
  return { setups: 0, cleanups: 0, replaySameNodes: true, nodes: new Map<string, HTMLElement>(),
    scheduled: 0, fired: 0, canceled: 0, pending: new Set<number>(),
    subscriptions: 0, removals: 0, listeners: new Set<Node>(), probes: 0,
    sources: { key: sourceAudit(), resize: sourceAudit(), replay: sourceAudit() } };
}
type Audit = ReturnType<typeof newAudit>;
let audit = newAudit();
// Track only the new adapter's own measurement probe, not library observers.
const NativeObserver = window.ResizeObserver;
window.ResizeObserver = class extends NativeObserver {
  private owner: Audit | undefined;
  override observe(target: Element, options?: ResizeObserverOptions) {
    if (target.classList.contains("session-content-top-size") && !this.owner) { this.owner = audit; this.owner.probes++; }
    super.observe(target, options);
  }
  override disconnect() { if (this.owner) { this.owner.probes--; this.owner = undefined; } super.disconnect(); }
};
// Observe only public subscription calls in this isolated page. Production has no
// audit props, exported model or test-only callbacks; original methods still run.
const setListener = Node.prototype.setEventListener, removeListener = Node.prototype.removeEventListener;
const listenerOwners = new Map<Node, Audit>();
const resizeReplays = new Map<Node, () => void>();
let recording: { owner: Audit; source: Source } | undefined;
Node.prototype.setEventListener = function (event, callback) {
  if (event !== "resize") { setListener.call(this, event, callback); return; }
  const owner = audit; // Captured registration lifetime, never the latest global audit.
  owner.subscriptions++; owner.listeners.add(this); listenerOwners.set(this, owner);
  let delivered = false, lastParams: unknown, lastThis: unknown;
  function invoke(context: unknown, params: unknown, source: "resize" | "replay") {
    const previous = recording; recording = { owner, source }; owner.sources[source].calls++;
    try { callback.call(context, params); } finally { recording = previous; }
  }
  resizeReplays.set(this, () => {
    if (!delivered) throw new Error("Replay requires a genuinely delivered public resize callback");
    invoke(lastThis, lastParams, "replay");
  });
  setListener.call(this, event, function (this: unknown, params: unknown) {
    delivered = true; lastParams = params; lastThis = this;
    invoke(this, params, "resize");
  });
};
Node.prototype.removeEventListener = function (event) {
  const owner = listenerOwners.get(this);
  if (event === "resize" && owner) {
    owner.removals++; owner.listeners.delete(this); listenerOwners.delete(this); resizeReplays.delete(this);
  }
  removeListener.call(this, event);
};
const frameOwners = new Map<number, NonNullable<typeof recording>>();
window.requestAnimationFrame = callback => {
  const record = recording, owner = record?.owner, source = record && owner!.sources[record.source];
  const id = nativeFrame(time => {
    if (owner && source) { owner.pending.delete(id); frameOwners.delete(id); owner.fired++; source.fired++; source.pending--; }
    callback(time);
  });
  if (owner && source && record) { owner.scheduled++; owner.pending.add(id); frameOwners.set(id, record); source.scheduled++; source.pending++; }
  return id;
};
window.cancelAnimationFrame = id => {
  const record = frameOwners.get(id);
  if (record) {
    const { owner, source } = record;
    owner.canceled++; owner.pending.delete(id); frameOwners.delete(id); owner.sources[source].canceled++; owner.sources[source].pending--;
  }
  nativeCancel(id);
};
function Pane({ name, label, owner }: { name: string; label: string; owner: Audit }) {
  const node = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const previous = owner.nodes.get(name);
    if (previous && previous !== node.current) owner.replaySameNodes = false;
    owner.nodes.set(name, node.current!); owner.setups++;
    return () => { owner.cleanups++; };
  }, [name, owner]);
  return <div ref={node} data-fake-pane={name} style={{ height: "100%" }}>
    <span>{label}:{name}</span><input aria-label={`${name} draft`} defaultValue="draft" />
  </div>;
}
let root = createRoot(container), label = "initial", single = false, appLayout = false;
let projection = { narrow: false, sessionsHidden: false, sessionWidth: 310 };
function render() {
  root.render(<StrictMode>{appLayout
    ? <SessionContentLayout {...projection}
      sessions={<aside className="session-rail" hidden={projection.sessionsHidden}><Pane name="left" label={label} owner={audit} /></aside>}
      content={<main className="content"><Pane name="right" label={label} owner={audit} /></main>}
      splitter={<div className="pane-splitter session-splitter" hidden={projection.narrow} role="separator" tabIndex={0}
        aria-label="Resize sessions" aria-orientation="vertical" aria-valuenow={projection.sessionWidth} />} />
    : single
    ? <WorkspaceLayout><Pane name="single" label={label} owner={audit} /></WorkspaceLayout>
    : <SplitWorkspaceLayout left={<Pane name="left" label={label} owner={audit} />}
      right={<Pane name="right" label={label} owner={audit} />} />}</StrictMode>);
}
function separator() { return container.querySelector<HTMLElement>(appLayout ? ".session-splitter" : '[role="separator"]')!; }
let retained: HTMLElement, panes: Element[] = [], inputs: HTMLInputElement[] = [];
function capture() {
  retained = separator(); panes = Array.from(container.querySelectorAll("[data-fake-pane]"));
  inputs = Array.from(container.querySelectorAll("input")); retained?.focus();
}
function stats(owner = audit) {
  return { setups: owner.setups, cleanups: owner.cleanups, replaySameNodes: owner.replaySameNodes,
    scheduled: owner.scheduled, fired: owner.fired, canceled: owner.canceled, pending: owner.pending.size,
    subscriptions: owner.subscriptions, removals: owner.removals, listeners: owner.listeners.size, probes: owner.probes,
    sources: { key: { ...owner.sources.key }, resize: { ...owner.sources.resize }, replay: { ...owner.sources.replay } } };
}
function rect(element: Element) {
  const r = element.getBoundingClientRect();
  return { x: r.x, y: r.y, width: r.width, height: r.height, right: r.right, bottom: r.bottom };
}
function snapshot() {
  const current = separator(), row = current.parentElement!, previous = current.previousElementSibling!, next = current.nextElementSibling!;
  const r = rect(row), p = rect(previous);
  return { row: r, previous: p, next: rect(next), separator: rect(current), container: rect(container),
    expected: String(Math.round(100 * (p.right - r.x) / r.width)),
    now: current.getAttribute("aria-valuenow"), text: current.getAttribute("aria-valuetext"),
    path: current.getAttribute("data-layout-path"), orientation: current.getAttribute("aria-orientation"),
    sameSeparator: current === retained, focused: document.activeElement === retained,
    samePanes: panes.length === 2 && panes.every((p, i) => p === container.querySelectorAll("[data-fake-pane]")[i]),
    sameInputs: inputs.length === 2 && inputs.every((p, i) => p === container.querySelectorAll("input")[i]),
    labels: Array.from(container.querySelectorAll("[data-fake-pane] span")).map(n => n.textContent),
    inputFocused: document.activeElement === inputs[0], inputValue: inputs[0]?.value, ...stats() };
}
function burst(count: number) {
  separator().focus(); const previous = recording; recording = { owner: audit, source: "key" };
  try {
    for (let i = 0; i < count; i++) {
      audit.sources.key.calls++;
      separator().dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true, cancelable: true }));
    }
  } finally { recording = previous; }
  return stats();
}
const disposed: Audit[] = [];
const fixture = {
  snapshot, capture, burst, stats,
  refresh() { label = "latest"; render(); },
  focusInput() { inputs[0].focus(); inputs[0].value = "owned draft"; },
  focusSeparator() { separator().focus(); },
  parentWidth(width: number) { container.style.width = `${width}px`; },
  replacePending(source: "key" | "replay" = "key") {
    const old = audit, oldNodes = [...inputs, retained];
    if (source === "key") burst(3);
    else {
      // Deterministic same-turn cancellation probe, NOT a browser resize delivery.
      // Reuse actual captured callback arguments without changing any geometry/model.
      const callbacks = Array.from(resizeReplays.values());
      if (callbacks.length !== 2) throw new Error("Expected two live resize subscriptions");
      for (let i = 0; i < 2; i++) for (const callback of callbacks) callback();
    }
    const pending = stats(); root.unmount(); disposed.push(old);
    const after = stats(old);
    audit = newAudit(); label = "initial"; root = createRoot(container); render();
    return { pending, disposed: after, disconnected: oldNodes.every(n => !n.isConnected) };
  },
  disposed: () => disposed.map(a => stats(a)),
  mountAppLayout() {
    root.unmount(); audit = newAudit(); appLayout = true; label = "initial";
    container.style.width = "100%"; root = createRoot(container); render();
  },
  projectAppLayout(narrow: boolean, sessionsHidden: boolean, sessionWidth: number) {
    projection = { narrow, sessionsHidden, sessionWidth }; render();
  },
  appSnapshot() {
    return { sessions: rect(container.querySelector(".session-rail")!), content: rect(container.querySelector(".content")!),
      bar: rect(separator()), aria: separator().getAttribute("aria-valuenow"),
      samePanes: panes.every((p, i) => p === container.querySelectorAll("[data-fake-pane]")[i]),
      sameInputs: inputs.every((p, i) => p === container.querySelectorAll("input")[i]),
      focused: document.activeElement === inputs[0], value: inputs[0]?.value,
      label: container.querySelector("[data-fake-pane] span")?.textContent, ...stats() };
  },
  settle: (frames = 3) => new Promise<void>(resolve => {
    const next = () => { if (--frames === 0) resolve(); else nativeFrame(next); }; nativeFrame(next);
  }),
  single() { root.unmount(); audit = newAudit(); single = true; label = "initial"; root = createRoot(container); render(); },
  singleSnapshot() {
    return { count: container.querySelectorAll(".flexlayout__tab").length,
      controls: container.querySelectorAll('[role="separator"],.flexlayout__tab_button,.flexlayout__tabset_header,.flexlayout__floating_window').length,
      sameInput: inputs[0] === container.querySelector("input"), focused: document.activeElement === inputs[0],
      value: inputs[0]?.value, label: container.querySelector("[data-fake-pane] span")?.textContent,
      width: container.querySelector(".flexlayout__tab")?.getBoundingClientRect().width, ...stats() };
  },
  unmount() { root.unmount(); return stats(); },
};
Object.assign(window, { splitFixture: fixture });
render();
export type SplitSnapshot = ReturnType<typeof snapshot>;
export type SplitStats = ReturnType<typeof stats>;
export type AppLayoutSnapshot = ReturnType<typeof fixture.appSnapshot>;
