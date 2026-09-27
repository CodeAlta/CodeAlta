import { createRoot } from "react-dom/client";
import { InventoryLanguageFixture } from "./inventoryLanguage.mount";
import { McpServersPanel } from "./McpServersPanel";
import type { McpInventoryResponse } from "#neoastra";

const root = createRoot(document.getElementById("app")!);
const pending: Array<{ sessionId: string; resolve: (value: McpInventoryResponse) => void; reject: (error: Error) => void }> = [];
const fixture = {
  pending, sessionId: "one", projectId: "project-one" as string | null, epoch: "e1",
  switchSession(sessionId: string, projectId: string | null) { fixture.sessionId = sessionId; fixture.projectId = projectId; render(); },
  reply(index: number, value: McpInventoryResponse) { pending[index].resolve(value); },
  fail(index: number) { pending[index].reject(new Error("private SECRET_ERROR")); },
};
Object.assign(window, { mcpFixture: fixture });
function render() {
  root.render(<InventoryLanguageFixture><McpServersPanel target={{ epoch: fixture.epoch, sessionId: fixture.sessionId, projectId: fixture.projectId }}
    read={request => new Promise((resolve, reject) => pending.push({ sessionId: request.sessionId, resolve, reject }))} /></InventoryLanguageFixture>);
}
render();
