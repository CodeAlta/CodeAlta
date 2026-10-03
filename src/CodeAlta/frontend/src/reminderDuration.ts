// Decimal whole seconds, a compact unit form (90s, 5m, 1h, 1h30m, 1d), HH:mm:ss (00-23 hours),
// or d.HH:mm:ss in invariant notation.
// The backend accepts only whole seconds in [1, 86400]; never round fractional input.
export function reminderDelaySeconds(value: string): number | null {
  const bounded = (seconds: number) => Number.isSafeInteger(seconds) && seconds >= 1 && seconds <= 86400 ? seconds : null;
  if (/^[1-9]\d*$/.test(value)) return bounded(Number(value));
  const units = /^(?:(\d+)d)?\s*(?:(\d+)h)?\s*(?:(\d+)m)?\s*(?:(\d+)s)?$/i.exec(value.trim());
  if (units && units.slice(1).some(part => part !== undefined))
    return bounded(Number(units[1] ?? "0") * 86400 + Number(units[2] ?? "0") * 3600 + Number(units[3] ?? "0") * 60 + Number(units[4] ?? "0"));
  const match = /^(?:(\d+)\.)?([01]\d|2[0-3]):([0-5]\d):([0-5]\d)$/.exec(value);
  if (!match) return null;
  return bounded(Number(match[1] ?? "0") * 86400 + Number(match[2]) * 3600 + Number(match[3]) * 60 + Number(match[4]));
}

/** Compact unit form of a whole-second delay, e.g. 5400 → "1h 30m". Round-trips through `reminderDelaySeconds`. */
export function formatReminderDelay(seconds: number): string {
  if (!Number.isSafeInteger(seconds) || seconds < 1) return String(seconds);
  const parts = [[86400, "d"], [3600, "h"], [60, "m"], [1, "s"]] as const;
  let rest = seconds;
  const out: string[] = [];
  for (const [size, unit] of parts) {
    const count = Math.floor(rest / size);
    if (count) { out.push(`${count}${unit}`); rest -= count * size; }
  }
  return out.join(" ");
}
