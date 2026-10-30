// A canvas drawn with React Flow. `@xyflow/react` is the one the window runs: its stylesheet is loaded already, and the window gives the
// graph its colors in both themes, so the script imports nothing else and sets no colors of React Flow itself.
//
// The pipeline lives in the plugin (plugin.cs). The script follows it with `useStream("flow.watch")` and keeps a copy in React Flow's state,
// so a drag is smooth; when a drag ends, a link is drawn or removed, or a step changes, it calls the plugin, whose answer comes back on the stream.
import { useEffect, useMemo, useState } from "react";
import { Button, ButtonGroup, Callout, Card, HTMLSelect, InputGroup, Spinner, Tag } from "@blueprintjs/core";
import { Background, Controls, Handle, MarkerType, MiniMap, Panel, Position, ReactFlow, useEdgesState, useNodesState } from "@xyflow/react";
import { Icon, html, useAlta, useStream, useTheme } from "codealta";

const kinds = { source: { icon: "git-commit-horizontal", series: 0 }, build: { icon: "hammer", series: 1 }, test: { icon: "flask-conical", series: 2 }, publish: { icon: "rocket", series: 3 } };
const statuses = { waiting: { intent: "none", label: "Waiting" }, running: { intent: "primary", label: "Running" }, done: { intent: "success", label: "Done" }, failed: { intent: "danger", label: "Failed" } };

// A step: a card with the color of its kind, its icon, what it runs and its status. The handles take the links in and out.
function StepNode({ data, selected }) {
    const theme = useTheme();
    const kind = kinds[data.kind] ?? kinds.build;
    const status = statuses[data.status] ?? statuses.waiting;
    const color = kindColor(theme, data.kind);
    const line = { whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" };
    return html`
        <div style=${{ width: 260, display: "flex", gap: 10, alignItems: "center", padding: "10px 12px", borderRadius: 10, background: theme.surface, color: theme.text,
            border: `1px solid ${selected ? color : theme.grid}`, boxShadow: selected ? `0 0 0 2px color-mix(in srgb, ${color} 35%, transparent)` : "0 6px 18px -12px #000",
            borderLeft: `4px solid ${color}`, opacity: data.status === "waiting" ? 0.8 : 1 }}>
            <${Handle} type="target" position=${Position.Left} />
            <span style=${{ flex: "none", display: "grid", placeItems: "center", width: 34, height: 34, borderRadius: 9, background: `color-mix(in srgb, ${color} 16%, transparent)`, color }}>
                <${Icon} name=${kind.icon} size=${17} />
            </span>
            <div style=${{ flex: 1, minWidth: 0, display: "flex", flexDirection: "column", gap: 3 }}>
                <div style=${{ display: "flex", alignItems: "center", gap: 6 }}>
                    <span style=${{ ...line, flex: 1, fontWeight: 600 }}>${data.title}</span>
                    <${Tag} minimal round intent=${status.intent} icon=${data.status === "running" ? html`<${Spinner} size=${10} />` : undefined}>${status.label}<//>
                </div>
                <span style=${{ ...line, color: theme.muted, fontSize: 11 }}>${data.detail}</span>
            </div>
            <${Handle} type="source" position=${Position.Right} />
        </div>`;
}

// Each kind of step has a color of the window's series, the same in the node, its links and the minimap.
const kindColor = (theme, kind) => theme.series[(kinds[kind] ?? kinds.build).series % theme.series.length];

// Outside the component: a new object at each render would mount every node again.
const nodeTypes = { step: StepNode };

const toNodes = (flow, selected) => flow.steps.map(step => ({ id: step.id, type: "step", position: { x: step.x, y: step.y }, selected: step.id === selected, data: step }));

// A link into a running step moves; a link out of a finished one has the color of its kind.
function toEdges(flow, theme) {
    const byId = new Map(flow.steps.map(step => [step.id, step]));
    return flow.links.map(link => {
        const from = byId.get(link.from), to = byId.get(link.to);
        const finished = from?.status === "done";
        const color = finished ? kindColor(theme, from.kind) : theme.muted;
        return {
            id: link.id, source: link.from, target: link.to, type: "smoothstep", animated: to?.status === "running",
            style: { stroke: color, strokeWidth: finished ? 2 : 1.25, strokeDasharray: finished || to?.status === "running" ? undefined : "4 4" },
            markerEnd: { type: MarkerType.ArrowClosed, color, width: 16, height: 16 },
        };
    });
}

export default function Pipeline() {
    const alta = useAlta();
    const theme = useTheme();
    const live = useStream("flow.watch");
    const flow = live.latest;
    const [nodes, setNodes, onNodesChange] = useNodesState([]);
    const [edges, setEdges, onEdgesChange] = useEdgesState([]);
    const [selected, setSelected] = useState(null);
    const [title, setTitle] = useState("");
    const [kind, setKind] = useState("test");
    const [problem, setProblem] = useState(null);

    // Each new pipeline of the plugin replaces the copy React Flow holds; the theme recolors the links.
    useEffect(() => { if (flow) setNodes(toNodes(flow, selected)); }, [flow, setNodes]); // eslint-disable-line react-hooks/exhaustive-deps
    useEffect(() => { if (flow) setEdges(toEdges(flow, theme)); }, [flow, theme, setEdges]);

    async function call(name, input) {
        setProblem(null);
        try { await alta.rpc.invoke(name, input, { signal: alta.closed }); }
        catch (error) { if (!alta.closed.aborted) setProblem(`${error.code}: ${error.message}`); }
    }

    const step = flow?.steps.find(item => item.id === selected);
    const counts = useMemo(() => Object.fromEntries(Object.keys(statuses).map(name => [name, flow?.steps.filter(item => item.status === name).length ?? 0])), [flow]);

    if (!flow) {
        return html`<div style=${{ padding: 16 }}>${live.error ? html`<${Callout} intent="danger" title="The pipeline did not load">${live.error.message}<//>` : "Loading the pipeline…"}</div>`;
    }

    const add = () => { if (title.trim()) { void call("flow.add", { title, kind }); setTitle(""); } };

    return html`
        <div style=${{ position: "absolute", inset: 0 }}>
            <${ReactFlow} nodes=${nodes} edges=${edges} nodeTypes=${nodeTypes} fitView fitViewOptions=${{ padding: 0.2 }} minZoom=${0.3}
                onNodesChange=${onNodesChange} onEdgesChange=${onEdgesChange}
                onNodeDragStop=${(_, node) => call("flow.move", { id: node.id, x: node.position.x, y: node.position.y })}
                onConnect=${connection => call("flow.connect", { from: connection.source, to: connection.target })}
                onEdgesDelete=${removed => removed.forEach(edge => call("flow.disconnect", { id: edge.id }))}
                onSelectionChange=${({ nodes: chosen }) => setSelected(chosen[0]?.id ?? null)}>
                <${Background} gap=${22} size=${1.4} />
                <${Controls} showInteractive=${false} position="bottom-left" />
                <${MiniMap} pannable zoomable position="bottom-right" nodeBorderRadius=${8} nodeColor=${node => kindColor(theme, node.data.kind)} />
                <${Panel} position="top-left">
                    <${Card} compact style=${{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                        <strong>${flow.title}</strong>
                        ${Object.entries(statuses).map(([name, status]) => html`<${Tag} key=${name} minimal round intent=${status.intent}>${counts[name]} ${status.label.toLowerCase()}<//>`)}
                        <${Button} size="small" variant="minimal" icon="reset" onClick=${() => call("flow.reset")}>Reset<//>
                    <//>
                <//>
                <${Panel} position="top-right">
                    <${Card} compact style=${{ width: 260, display: "flex", flexDirection: "column", gap: 8 }}>
                        ${step ? html`
                            <div style=${{ display: "flex", gap: 8, alignItems: "center" }}>
                                <${Icon} name=${(kinds[step.kind] ?? kinds.build).icon} />
                                <strong style=${{ flex: 1 }}>${step.title}</strong>
                                <${Tag} minimal round intent=${statuses[step.status].intent}>${statuses[step.status].label}<//>
                            </div>
                            <span className="alta-muted">${step.detail}</span>
                            <${ButtonGroup} fill>
                                <${Button} size="small" icon="play" disabled=${step.status === "running"} onClick=${() => call("flow.status", { step: step.id, status: "running" })}>Start<//>
                                <${Button} size="small" icon="tick" intent="success" disabled=${step.status === "done"} onClick=${() => call("flow.status", { step: step.id, status: "done" })}>Done<//>
                                <${Button} size="small" icon="cross" intent="danger" disabled=${step.status === "failed"} onClick=${() => call("flow.status", { step: step.id, status: "failed" })}>Fail<//>
                            <//>` : html`<span className="alta-muted">Select a step to start or finish it. Drag from a handle to link two steps; select a link and press Delete to remove it.</span>`}
                        <div style=${{ display: "flex", gap: 6 }}>
                            <${InputGroup} small fill placeholder="A new step" value=${title} onChange=${event => setTitle(event.target.value)} onKeyDown=${event => { if (event.key === "Enter") add(); }} />
                            <${HTMLSelect} minimal value=${kind} style=${{ minWidth: 92 }} onChange=${event => setKind(event.target.value)} options=${Object.keys(kinds)} />
                            <${Button} size="small" icon="add" onClick=${add} />
                        </div>
                        ${problem && html`<${Callout} compact intent="warning" icon="warning-sign">${problem}<//>`}
                    <//>
                <//>
            <//>
        </div>`;
}
