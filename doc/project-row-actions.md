# Desktop project-row actions

In the development desktop, reveal **Projects** if the rail is hidden. Each saved
project has an ellipsis actions button. You can also right-click its row or focus
the row and press **Shift+F10** or the **Context Menu** key. Opening the menu does
not select a project, import a folder, refresh the catalog or send a request.

- **Open** uses the existing saved-project navigation. It does not import or start
  work. Archived and catalog-only projects remain read-only.
- **Details** displays that row's exact saved catalog snapshot without changing
  the selected project or session. ID and path can be copied explicitly. The
  snapshot does not establish branch, live activity, source revision or lifecycle.
- **Rename project…** is available only for the already-selected, nonarchived
  project with an owned, currently permitted host. It enters the existing exact
  source/revision preflight, then shows the name in a popover beside the row;
  saving remains separate.
- **Archive project… / Unarchive project…** is available only for the
  already-selected project with current permission. It asks in a popover beside
  the row, unless the user answered **Do not ask again**; the write stays bound to
  the revision read just before it. Archiving changes catalog metadata, not project
  files or running work. Unarchive does not start work.

For a nonselected row, mutation items are disabled: opening its menu never silently
switches scope to authorize a write. Existing rename/F2 and archive controls remain.
Pending or unconfirmed original operations continue to block fresh menu mutations;
closing a menu or dialog does not cancel, retry or resolve those operations.

Use Up/Down, Home/End and Enter within the menu; Escape dismisses it and restores
focus when the original row is still current. Repeated/composing activation keys
are ignored. Changing catalog, host, selection, refresh evidence or modal lifetime
retires old actions even if an identical identity later returns. Missing or
ambiguous project IDs/paths do not authorize actions. Other modal transitions and
an explicit Details close/reopen invalidate stale Copy controls synchronously.

Chrome supports English, Spanish, French, German, Japanese and Simplified Chinese.
Language changes do not refresh the catalog or create new action lifetimes.
Browser tests use disposable fake-host fixtures, including narrow light/dark
layouts; they do not qualify native WebView keyboard or screen-reader behavior.
No project deletion, bulk operation, backend API, configuration editing or new
source-write authority is included.
