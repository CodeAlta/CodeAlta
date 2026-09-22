export function sessionTime(value: string, now = Date.now()): { label: string; title: string; dateTime?: string } {
  const date = new Date(value);
  const timestamp = date.getTime();
  if (!Number.isFinite(timestamp)) return { label: value, title: value, dateTime: undefined };

  const seconds = Math.round(Math.abs(now - timestamp) / 1000);
  const future = timestamp > now;
  let label = seconds < 5 ? "just now" : `${seconds}s ${future ? "from now" : "ago"}`;
  if (seconds >= 60) {
    const units = [
      [31536000, "y"], [2592000, "mo"], [604800, "w"],
      [86400, "d"], [3600, "h"], [60, "min"],
    ] as const;
    const [duration, unit] = units.find(([size]) => seconds >= size)!;
    const count = Math.floor(seconds / duration);
    label = count === 1 && unit === "d" ? future ? "tomorrow" : "yesterday"
      : `${future ? "in " : ""}${count}${unit}${future ? "" : " ago"}`;
  }
  return { label, title: date.toString(), dateTime: date.toISOString() };
}
