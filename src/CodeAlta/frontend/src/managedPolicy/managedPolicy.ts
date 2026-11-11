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

/** How often the policy of a host is read again: soon while a reading is under way, else now and then. */
export const managedPolicyPollMilliseconds = Object.freeze({ underWay: 1_500, idle: 15_000 });

/** The timers a poller schedules its reads with; given, so that tests drive the time. */
export type PollTimers = Readonly<{ set: (callback: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;
const realTimers: PollTimers = Object.freeze({ set: (callback, milliseconds) => setTimeout(callback, milliseconds), clear: timer => clearTimeout(timer as ReturnType<typeof setTimeout>) });

/**
 * Reads the policy of the host of one epoch: at once, then again after each read, failed ones included, soon while a
 * reading is under way. A policy can arrive while a page shows (the first reading a moment after the start). Once
 * stopped, as when the host of another epoch takes over, nothing it reads or refreshes is given any more.
 */
export function createManagedPolicyPoller(epoch: string, api: ManagedPolicyApi, onChange: (policy: ManagedPolicyResponse | null, refreshing: boolean) => void,
  timers: PollTimers = realTimers) {
  let stopped = false;
  let timer: unknown = null;
  let refreshing = false;
  let policy: ManagedPolicyResponse | null = null;
  const abort = new AbortController();
  const publish = () => { if (!stopped) onChange(policy, refreshing || readingUnderWay(policy?.copilot)); };
  const schedule = () => {
    if (stopped) return;
    if (timer !== null) timers.clear(timer);
    timer = timers.set(() => { timer = null; void read(); },
      refreshing || readingUnderWay(policy?.copilot) ? managedPolicyPollMilliseconds.underWay : managedPolicyPollMilliseconds.idle);
  };
  const read = async () => {
    try {
      const value = await api.read({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 8_000 });
      if (!stopped) { policy = value; publish(); }
    } catch {
      // The page shows what it read last; the next read says where it stands.
    } finally {
      schedule();
    }
  };
  void read();
  return {
    /** Reads the managed settings of the accounts again; the first reading downloads the runtime and can take minutes. */
    refresh() {
      if (stopped || refreshing) return;
      refreshing = true;
      publish();
      schedule();
      void api.refresh({ expectedEpoch: epoch }, { timeoutMilliseconds: 600_000 })
        .then(value => { if (!stopped) policy = value; }, () => { /* The next read says where it stands. */ })
        .finally(() => { refreshing = false; publish(); schedule(); });
    },
    /** Stops reading: what is still on its way is dropped. */
    stop() {
      stopped = true;
      abort.abort();
      if (timer !== null) timers.clear(timer);
      timer = null;
    },
  };
}

/** The policy of a host, read when the component shows, then every 15 seconds, every second and a half while its reading is under way. */
export function useManagedPolicy(epoch: string | null, api: ManagedPolicyApi = managedPolicy) {
  const [state, setState] = useState<Readonly<{ policy: ManagedPolicyResponse | null; refreshing: boolean }>>({ policy: null, refreshing: false });
  const poller = useRef<ReturnType<typeof createManagedPolicyPoller> | null>(null);
  useEffect(() => {
    setState({ policy: null, refreshing: false });
    if (!epoch) return;
    // One poller per host: the answers of the host of an earlier epoch are dropped with its poller.
    const current = createManagedPolicyPoller(epoch, api, (policy, refreshing) => setState({ policy, refreshing }));
    poller.current = current;
    return () => { current.stop(); if (poller.current === current) poller.current = null; };
  }, [api, epoch]);
  const refresh = useCallback(() => poller.current?.refresh(), []);
  return { policy: state.policy, refreshing: state.refreshing, refresh };
}
