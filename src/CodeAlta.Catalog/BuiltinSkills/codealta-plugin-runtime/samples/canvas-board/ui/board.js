// A canvas as a React component. The window draws the default export in its own tree, so the libraries below are the ones the application
// runs (one React, one Blueprint) and what opens (a menu, a popover) opens in the layer of the window, above the tabs.
//
// The board lives in the plugin (plugin.cs). This script reads it with `alta.rpc`: `useRpc` makes a call, `useStream` follows a stream the
// plugin pushes to, `alta.rpc.subscribe` hears an event, and `alta.rpc.invoke` changes the board. A call fails with an `AltaError` that has
// a `code` and a `retryable` mark; a plugin's own errors (`invalid_card`, `not_found`) are told apart from the window's (`connection_closed`).
import { useEffect, useState } from "react";
import { Button, Callout, Card, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext, Tab, Tabs, Tag } from "@blueprintjs/core";
import { Chart, FileLink, Icon, Markdown, html, useAlta, useRpc, useStream, useVisible } from "codealta";

const columnNames = ["To do", "Doing", "Done"];

export default function Board() {
    const alta = useAlta();
    const visible = useVisible();
    // The first read, and then the stream: the plugin sends the board now and again after each change, and a hidden tab pauses it.
    const first = useRpc("board.get");
    const live = useStream("board.watch");
    const board = live.latest ?? first.data;
    const [tab, setTab] = useState("cards");
    const [title, setTitle] = useState("");
    const [problem, setProblem] = useState(null);
    const [lastChange, setLastChange] = useState(null);

    // An event is not kept: the script subscribes, and reads the board with a call (above) for what it missed.
    useEffect(() => {
        let stop = null;
        let ended = false;
        alta.rpc.subscribe("board.changed", change => setLastChange(change.message)).then(
            unsubscribe => { if (ended) unsubscribe(); else stop = unsubscribe; },
            () => { /* The stream above shows the failure. */ });
        return () => { ended = true; stop?.(); };
    }, [alta]);

    // The calls that change the board answer with the new board; the stream shows it too, so the answer is only checked for an error.
    async function call(name, input) {
        setProblem(null);
        try { await alta.rpc.invoke(name, input, { signal: alta.closed }); }
        catch (error) { if (!alta.closed.aborted) setProblem(`${error.code}: ${error.message}`); }
    }

    const add = () => { if (title) { void call("board.add", { column: columnNames[0], title }); setTitle(""); } };

    if (!board) {
        return html`<div style=${{ padding: 16 }}>${first.error || live.error ? html`<${Callout} intent="danger" title="The board did not load">${(first.error ?? live.error).message}<//>` : "Loading the board…"}</div>`;
    }

    // The counts are plain data: a chart option is JSON, and the window gives it its colors, its legend and its description for a reader.
    const option = {
        xAxis: { type: "category", data: board.columns.map(column => column.name) },
        yAxis: { type: "value", minInterval: 1 },
        series: [{ name: "Cards", type: "bar", data: board.columns.map(column => column.cards.length) }],
    };

    // The link shows the name of the file, which fits a narrow column; its tooltip is the whole path.
    const card = (item, column) => html`
        <${Card} key=${item.id} compact style=${{ display: "flex", alignItems: "center", gap: 6 }}>
            <${Icon} name="box" />
            <span style=${{ flex: 1 }}>${item.title}${item.file && html` <${FileLink} path=${item.file} line=${item.line}>${item.file.split("/").pop()}<//>`}</span>
            ${column < columnNames.length - 1 && html`<${Button} size="small" variant="minimal" icon="arrow-right" title=${`Move to ${columnNames[column + 1]}`}
                onClick=${() => call("board.move", { id: item.id, column: columnNames[column + 1] })} />`}
        <//>`;

    return html`
        <div style=${{ padding: 16, display: "flex", flexDirection: "column", gap: 12 }}>
            <div style=${{ display: "flex", gap: 8, alignItems: "center" }}>
                <h3 style=${{ margin: 0 }}>${board.title}</h3>
                <${Tag} minimal>${visible ? "shown" : "hidden"}<//>
                <${Tag} minimal>revision ${board.revision}<//>
                ${lastChange && html`<span className="alta-muted">${lastChange}</span>`}
                <${PopoverNext} placement="bottom-start" content=${html`
                    <${Menu}>
                        <${MenuItem} icon="git-branch" text="Show the changes" onClick=${() => alta.host.openDiff()} />
                        <${MenuItem} icon="tag" text="Mark the tab" onClick=${() => alta.host.setBadge(board.columns.reduce((sum, column) => sum + column.cards.length, 0))} />
                        <${MenuItem} icon="cross" text="Clear the mark" onClick=${() => alta.host.setBadge(null)} />
                        <${MenuDivider} />
                        <${MenuItem} icon="error" text="Call a handler that throws" onClick=${() => call("board.explode")} />
                    <//>`}>
                    <${Button} icon="menu" size="small">Actions<//>
                <//>
            </div>
            <div style=${{ display: "flex", gap: 8 }}>
                <${InputGroup} small placeholder="A new card" value=${title} onChange=${event => setTitle(event.target.value)} onKeyDown=${event => { if (event.key === "Enter") add(); }} />
                <${Button} small icon="add" onClick=${add}>Add<//>
            </div>
            ${problem && html`<${Callout} intent="warning" icon="warning-sign">${problem}<//>`}
            <${Tabs} id="board-tabs" selectedTabId=${tab} onChange=${id => setTab(String(id))}>
                <${Tab} id="cards" title="Cards" panel=${html`
                    <div style=${{ display: "flex", gap: 12, alignItems: "flex-start" }}>
                        ${board.columns.map((column, index) => html`
                            <div key=${column.name} style=${{ flex: 1, display: "flex", flexDirection: "column", gap: 8 }}>
                                <strong>${column.name} <${Tag} round minimal>${column.cards.length}<//></strong>
                                ${column.cards.map(item => card(item, index))}
                            </div>`)}
                    </div>`} />
                <${Tab} id="chart" title="Chart" panel=${html`<${Chart} option=${option} ariaLabel="Cards in each column" height=${220} visible=${visible} />`} />
                <${Tab} id="notes" title="Notes" panel=${html`<${Markdown} source=${"The board lives in the plugin (`plugin.cs`) and is read with `alta.rpc`: a call, a stream and an event. Add a card from a terminal with `alta board add --title \"...\"`."} />`} />
            <//>
        </div>`;
}
