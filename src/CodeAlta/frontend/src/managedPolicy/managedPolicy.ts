import { useCallback, useEffect, useRef, useState } from "react";
import { managedPolicy, type ManagedPolicyReading, type ManagedPolicyResponse } from "#neoastra";
import type { MessageKey } from "../localization";

/** The calls of the policy of the organization a component makes: given, so that tests mount it alone. */
export type ManagedPolicyApi = Pick<typeof managedPolicy, "read" | "refresh">;

/** One thing the policy of the organization enforces, as a sentence; `mode` names a permission mode the sentence shows. */
export type ManagedPolicyLine = Readonly<{ key: MessageKey; mode?: string }>;

/** What the policy of the organization enforces, one sentence each, in the order of the page. */
export function managedPolicyLines(policy: ManagedPolicyResponse | null): readonly ManagedPolicyLine[] {
  if (!policy?.managed) return [];
  const lines: ManagedPolicyLine[] = [];
  if (policy.freshPolicyMissing) lines.push({ key: "No session runs until the policy is read again." });
  if (policy.bypassPermissionsDisabled) lines.push({ key: "Bypass permissions is turned off." });
  if (policy.assistedPermissionsDisabled) lines.push({ key: "Auto is turned off." });
  if (policy.defaultPermissionMode) lines.push({ key: "New sessions start in {mode}.", mode: policy.defaultPermissionMode });
  if (policy.sandboxRequired) lines.push({ key: "Every session runs in the sandbox." });
  if (policy.runOutsideSandboxDisallowed) lines.push({ key: "No command runs outside the sandbox." });
  return lines;
}

/** Whether the reading of the Copilot accounts is under way: its runtime is downloaded, or the runtime is asked. */
export function readingUnderWay(reading: ManagedPolicyReading | null | undefined): boolean {
  return reading?.state === "installing" || reading?.state === "reading";
}

/** The size of a download, in whole megabytes, for `{received} of {total} MB`; null without a size. */
export function downloadSize(reading: ManagedPolicyReading | null | undefined): Readonly<{ received: string; total: string }> | null {
  if (reading?.state !== "installing" || reading.totalKilobytes == null || reading.totalKilobytes <= 0) return null;
  const megabytes = (kilobytes: number) => String(Math.round(kilobytes / 1_024));
  return { received: megabytes(Math.min(reading.receivedKilobytes ?? 0, reading.totalKilobytes)), total: megabytes(reading.totalKilobytes) };
}

/** The policy of a host, read when the component shows, then every 15 seconds, every second and a half while its reading is under way. */
export function useManagedPolicy(epoch: string | null, api: ManagedPolicyApi = managedPolicy) {
  const [policy, setPolicy] = useState<ManagedPolicyResponse | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [read, setRead] = useState(0);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => { setPolicy(null); }, [epoch]);
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    void api.read({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 8_000 })
      .then(value => { if (!abort.signal.aborted) setPolicy(value); }, () => { /* The page shows what it read last. */ });
    return () => abort.abort();
  }, [api, epoch, read]);
  const underWay = refreshing || readingUnderWay(policy?.copilot);
  // A policy can arrive while the page is shown (the first reading a moment after the start): it is read again, soon
  // while a reading is under way. The read is the policy the host holds, not a reading of the accounts.
  useEffect(() => {
    if (!epoch) return;
    const timer = setTimeout(() => setRead(value => value + 1), underWay ? 1_500 : 15_000);
    return () => clearTimeout(timer);
  }, [epoch, underWay, policy]);
  const refresh = useCallback(() => {
    if (!epoch) return;
    setRefreshing(true);
    // The first reading downloads the runtime: it can take minutes, while the reads above show its progress.
    void api.refresh({ expectedEpoch: epoch }, { timeoutMilliseconds: 600_000 })
      .then(value => { if (alive.current) setPolicy(value); }, () => { /* The next read says where it stands. */ })
      .finally(() => { if (alive.current) { setRefreshing(false); setRead(value => value + 1); } });
  }, [api, epoch]);
  return { policy, refreshing: underWay, refresh };
}
