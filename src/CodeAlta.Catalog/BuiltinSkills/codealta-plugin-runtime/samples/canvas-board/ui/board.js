// A canvas as a React component. The window draws the default export in its own tree, so the libraries below are the ones the application
// runs (one React, one Blueprint) and what opens (a menu, a popover) opens in the layer of the window, above the tabs.
import { useState } from "react";
import { Button, Callout, Card, Menu, MenuDivider, MenuItem, PopoverNext, Tab, Tabs, Tag } from "@blueprintjs/core";
import { Chart, FileLink, Icon, Markdown, html, useAlta, useVisible } from "codealta";

const fallback = { title: "Board", columns: [{ name: "To do", cards: [{ title: "Pass an input to the canvas" }] }] };

export default function Board() {
    const alta = useAlta();
    const visible = useVisible();
    const board = { ...fallback, ...(alta.context.input ?? {}) };
    const [tab, setTab] = useState("cards");
    const [picked, setPicked] = useState(null);

    // The counts are plain data: a chart option is JSON, and the window gives it its colors, its legend and its description for a reader.
    const option = {
        xAxis: { type: "category", data: board.columns.map(column => column.name) },
        yAxis: { type: "value", minInterval: 1 },
        series: [{ name: "Cards", type: "bar", data: board.columns.map(column => column.cards.length) }],
    };

    const card = (item, index) => html`
        <${Card} key=${index} interactive compact onClick=${() => setPicked(item)}>
            <${Icon} name="box" /> ${item.title}
            ${item.file && html` <${FileLink} path=${item.file} line=${item.line}>${item.file}<//>`}
        <//>`;

    return html`
        <div style=${{ padding: 16, display: "flex", flexDirection: "column", gap: 12 }}>
            <div style=${{ display: "flex", gap: 8, alignItems: "center" }}>
                <h3 style=${{ margin: 0 }}>${board.title}</h3>
                <${Tag} minimal>${visible ? "shown" : "hidden"}<//>
                <${PopoverNext} placement="bottom-start" content=${html`
                    <${Menu}>
                        <${MenuItem} icon="git-branch" text="Show the changes" onClick=${() => alta.host.openDiff()} />
                        <${MenuItem} icon="notifications" text="Say hello" onClick=${() => alta.host.notify("Hello from the board", { tone: "success" })} />
                        <${MenuDivider} />
                        <${MenuItem} icon="tag" text="Mark the tab" onClick=${() => alta.host.setBadge(board.columns.reduce((sum, column) => sum + column.cards.length, 0))} />
                        <${MenuItem} icon="cross" text="Clear the mark" onClick=${() => alta.host.setBadge(null)} />
                    <//>`}>
                    <${Button} icon="menu" size="small">Actions<//>
                <//>
            </div>
            <${Tabs} id="board-tabs" selectedTabId=${tab} onChange=${id => setTab(String(id))}>
                <${Tab} id="cards" title="Cards" panel=${html`
                    <div style=${{ display: "flex", gap: 12, alignItems: "flex-start" }}>
                        ${board.columns.map(column => html`
                            <div key=${column.name} style=${{ flex: 1, display: "flex", flexDirection: "column", gap: 8 }}>
                                <strong>${column.name} <${Tag} round minimal>${column.cards.length}<//></strong>
                                ${column.cards.map(card)}
                            </div>`)}
                    </div>`} />
                <${Tab} id="chart" title="Chart" panel=${html`<${Chart} option=${option} ariaLabel="Cards in each column" height=${220} visible=${visible} />`} />
                <${Tab} id="notes" title="Notes" panel=${html`<${Markdown} source=${"The board is drawn by `ui/board.js`: a React component with **Blueprint**, a chart and the `alta` object."} />`} />
            <//>
            ${picked && html`<${Callout} intent="primary" title=${picked.title} icon="info-sign">
                <${Button} size="small" onClick=${() => setPicked(null)}>Close<//>
            <//>`}
        </div>`;
}
