/**
 * The line that runs a command in the terminal of the user: PowerShell on Windows, a POSIX shell elsewhere, with its
 * environment variables set for it.
 */
export function terminalCommandLine(parts: readonly string[], environment: readonly Readonly<{ name: string; value: string }>[], windows: boolean): string {
  if (parts.length === 0) return "";
  if (windows) {
    const quote = (value: string) => /^[\w.:\\/@+=-]+$/.test(value) ? value : `'${value.replaceAll("'", "''")}'`;
    const variables = environment.map(entry => `$env:${entry.name}=${quote(entry.value)}; `).join("");
    // A quoted program is run with the call operator.
    const program = quote(parts[0]);
    return `${variables}${program.startsWith("'") ? `& ${program}` : program}${parts.slice(1).map(part => ` ${quote(part)}`).join("")}`;
  }

  const quote = (value: string) => /^[\w.:/@+=-]+$/.test(value) ? value : `'${value.replaceAll("'", `'\\''`)}'`;
  const variables = environment.map(entry => `${entry.name}=${quote(entry.value)} `).join("");
  return `${variables}${parts.map(quote).join(" ")}`;
}
