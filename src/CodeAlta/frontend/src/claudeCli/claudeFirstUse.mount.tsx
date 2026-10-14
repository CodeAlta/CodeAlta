import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { ClaudeCliCheckResponse, ClaudeCliInstallState } from "#neoastra";
import { ClaudeCliFirstUse } from "./ClaudeCliFirstUse";

// A Claude Code provider without a CLI (`claude-missing`), one that has one (`claude-code`), and one of another type.
const check = (providerKey: string, executable: string | null, claude = true): ClaudeCliCheckResponse => ({ status: "ok", providerKey, claude, executable,
  configDir: "~/.claude-work", supported: true, version: "2.1.292", size: "113796621", windows: true, home: "C:\\Users\\me" });
const pinned = "C:\\Users\\me\\.alta\\cache\\claude-code\\2.1.292\\win-x64\\claude.exe";
const state = { installed: false, installs: 0, cancels: 0, probes: [] as string[], probeReason: "claude-code-signed-out" as string | null,
  tell: (_state: ClaudeCliInstallState) => { }, select: (_key: string) => { } };
const api = {
  check: async ({ providerKey }: { providerKey: string | null }) => providerKey === "codex" ? check("codex", null, false)
    : providerKey === "claude-code" ? check("claude-code", "C:\\Users\\me\\.local\\bin\\claude.exe") : check(providerKey ?? "", state.installed ? pinned : null),
  install: async () => { state.installs++; return { status: "ok" }; },
  cancelInstall: async () => { state.cancels++; return { status: "ok" }; },
  watch: async (_request: object, options?: { signal?: AbortSignal }) => {
    const waiting: ClaudeCliInstallState[] = [{ state: "idle", receivedBytes: "0", totalBytes: "0", error: null }];
    let wake = () => { };
    state.tell = value => { waiting.push(value); wake(); };
    options?.signal?.addEventListener("abort", () => wake(), { once: true });
    return (async function* () {
      while (!options?.signal?.aborted) {
        if (waiting.length === 0) await new Promise<void>(resolve => { wake = resolve; });
        while (waiting.length > 0) yield waiting.shift()!;
      }
    })();
  },
};
const probe = async (request: { providerId: string }) => {
  state.probes.push(request.providerId);
  return { status: "ok", epoch: "epoch", providerId: request.providerId, availability: state.probeReason ? "Failed" : "Ready", reason: state.probeReason };
};
Object.assign(window, { firstUseFixture: state });

function Composer() {
  const [providerKey, setProviderKey] = useState("claude-missing");
  state.select = setProviderKey;
  return <div className="composer-region" style={{ width: 700, padding: 16 }}><ClaudeCliFirstUse epoch="epoch" providerKey={providerKey} api={api} probe={probe} /></div>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Composer /></StrictMode>);
