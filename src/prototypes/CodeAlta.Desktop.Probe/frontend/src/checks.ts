export function check(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}

export async function eventually(predicate: () => boolean | Promise<boolean>, label: string, milliseconds = 8_000): Promise<void> {
  const deadline = Date.now() + milliseconds;
  while (!(await predicate())) {
    if (Date.now() >= deadline) throw new Error(`Timed out: ${label}`);
    await new Promise(resolve => setTimeout(resolve, 25));
  }
}
