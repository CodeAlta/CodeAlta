import { createRoot } from "react-dom/client";
import { ReminderPanel } from "./ReminderPanel";
import { createReminderActions } from "./reminderActions";
import type { ReminderDetailRequest, ReminderDetailResponse, ReminderListResponse, ReminderMutationResponse } from "#neoastra";

const reads: Array<{ sessionId: string; resolve: (value: ReminderListResponse) => void; reject: (error: Error) => void }> = [];
const details: Array<{ request: ReminderDetailRequest; resolve: (value: ReminderDetailResponse) => void; reject: (error: Error) => void }> = [];
const writes: Array<{ request: unknown; resolve: (value: ReminderMutationResponse) => void; reject: (error: Error) => void }> = [];
const actions = createReminderActions(request => new Promise((resolve, reject) => writes.push({ request, resolve, reject })),
  request => new Promise((resolve, reject) => writes.push({ request, resolve, reject })));
const root = createRoot(document.getElementById("app")!);
const fixture = {
  reads, details, writes, epoch: "e1" as string | null, sessionId: "one" as string | null, mounted: true,
  session(id: string | null) { fixture.sessionId = id; render(); },
  host(epoch: string | null) { fixture.epoch = epoch; render(); },
  leave() { fixture.mounted = false; render(); },
  open() { fixture.mounted = true; render(); },
};
Object.assign(window, { reminderFixture: fixture });
function render() {
  if (!fixture.mounted) { root.render(<div>Other screen</div>); return; }
  root.render(<ReminderPanel key={JSON.stringify([fixture.epoch, fixture.sessionId])}
    target={fixture.epoch && fixture.sessionId ? { epoch: fixture.epoch, sessionId: fixture.sessionId } : null}
    actions={actions} mutationAllowed={true} canMutate={() => true}
    readDetail={request => new Promise((resolve, reject) => details.push({ request, resolve, reject }))}
    read={request => new Promise((resolve, reject) => reads.push({ sessionId: request.sessionId, resolve, reject }))} />);
}
render();
