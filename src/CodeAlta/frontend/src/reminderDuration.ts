// Decimal whole seconds, HH:mm:ss (00-23 hours), or d.HH:mm:ss in invariant notation.
// The backend accepts only whole seconds in [1, 86400]; never round fractional input.
export function reminderDelaySeconds(value: string): number | null {
  if (/^[1-9]\d*$/.test(value)) {
    const seconds = Number(value);
    return Number.isSafeInteger(seconds) && seconds <= 86400 ? seconds : null;
  }
  const match = /^(?:(\d+)\.)?([01]\d|2[0-3]):([0-5]\d):([0-5]\d)$/.exec(value);
  if (!match) return null;
  const days = Number(match[1] ?? "0");
  const seconds = days * 86400 + Number(match[2]) * 3600 + Number(match[3]) * 60 + Number(match[4]);
  return Number.isSafeInteger(seconds) && seconds >= 1 && seconds <= 86400 ? seconds : null;
}
