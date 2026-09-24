// Shared workspace chord action; invoked by the app's existing shortcut guards.
export function activateContextShortcut(root: ParentNode | null): void {
  const refresh = root?.querySelector<HTMLButtonElement>(".owned-session #refresh-session-context");
  if (refresh) {
    const diagnostics = refresh.closest<HTMLDetailsElement>("details");
    if (diagnostics) diagnostics.open = true;
    refresh.focus();
    refresh.click();
    return;
  }
  root?.querySelector<HTMLButtonElement>(".catalog-composer button.prompt-state")?.click();
}
