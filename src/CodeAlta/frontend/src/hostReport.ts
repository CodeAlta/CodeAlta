import { boot } from "#neoastra";

/** The most reports the page sends to the log of the host in a minute; the others are counted and said with the next one. */
export const reportsPerMinute = 30;

export type HostReport = (level: "warning" | "information", message: string) => void;

/**
 * Writes what the page notices about its host (a slow call, a host that does not answer) to the console and to the log
 * of the host, so that CodeAlta can be investigated from its log. A report waits for the host like any call: one written
 * while the host is busy reaches the log once it answers, with the times it measured. Never throws.
 */
export function createHostReport(send: (level: string, message: string) => Promise<unknown>, now: () => number = () => Date.now()): HostReport {
  let windowStart = Number.NEGATIVE_INFINITY;
  let sent = 0;
  let dropped = 0;
  return (level, message) => {
    try {
      (level === "warning" ? console.warn : console.info)(`[CodeAlta host] ${message}`);
      const time = now();
      if (time - windowStart >= 60_000) { windowStart = time; sent = 0; }
      if (sent >= reportsPerMinute) { dropped++; return; }
      sent++;
      const text = dropped > 0 ? `${message} (${dropped} more reports were left out)` : message;
      dropped = 0;
      void send(level, text).catch(() => { /* The host is gone: the console has it. */ });
    } catch { /* A report never breaks what reports. */ }
  };
}

/** The report of this page to its host. */
export const reportToHost: HostReport = createHostReport((level, message) => boot.report({ level, message }));
