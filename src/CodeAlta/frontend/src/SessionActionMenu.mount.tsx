import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { SessionActionMenu } from "./SessionActionMenu";

const requests: string[] = [];
Object.assign(window, { sessionMenuRequests: requests });
createRoot(document.getElementById("root")!).render(<StrictMode>
  <SessionActionMenu id="enabled" label="Enabled session" rename deleteAllowed menuRef={null}
    onAction={action => requests.push(action)} onDismiss={() => {}} />
  <SessionActionMenu id="disabled" label="Read-only session" rename={false} deleteAllowed={false} menuRef={null}
    onAction={action => requests.push(`read-only:${action}`)} onDismiss={() => {}} />
</StrictMode>);
