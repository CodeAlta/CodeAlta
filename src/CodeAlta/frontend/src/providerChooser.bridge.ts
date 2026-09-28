// Isolated browser fixture only; no production imports this module.
import type { SessionProviderRequest } from "#neoastra";
export type * from "../../obj/neoastra/neoastra";
const requests: SessionProviderRequest[] = [];
let provider = "original";
let status = "ok", hold = false;
let release: (() => void) | undefined;
export const sessionOperations = {
  providerChoices: async () => ({ status: "ok", epoch: "fixture", sessionId: "one", runtimeInstanceId: "00000000-0000-0000-0000-000000000001",
    attachmentGeneration: "2", providerKey: provider, revision: "42", providers: [{ id: "original", name: "Original" }, { id: "target", name: "Target" }] }),
  selectProvider: async (request: SessionProviderRequest) => {
    requests.push(request);
    if (hold) { hold = false; await new Promise<void>(resolve => { release = resolve; }); }
    if (status === "ok") provider = request.providerKey;
    return { status, epoch: "fixture", sessionId: "one" };
  },
};
Object.assign(window, { providerBridge: { requests, provider: () => provider,
  status: (value: string) => { status = value; }, hold: () => { hold = true; }, release: () => release?.() } });
