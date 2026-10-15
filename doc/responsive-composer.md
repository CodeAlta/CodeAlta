# Composer layout

## The prompt surface and its widths (#145)

`ComposerSurface` (`src/CodeAlta/frontend/src/ComposerSurface.tsx`) draws every composer (owned, new session,
catalog) as three rows: the run status and the background tasks with the Workspace chip and the plugin status
(`ComposerChrome.context`, `.status`) above the editor; the editor; and the Agent chip, the meters (sub-agents
from `ComposerChrome.subAgents`, then context ring and prompt cache), the `⋯` menu and the actions below.

The surface measures its own width with a `ResizeObserver` (`useMeasuredDensity`, `promptSurface/PromptSurfaceParts.tsx`)
and sets `data-density`: `wide` from 1040 px, `medium` from 780, `narrow` from 540, `tiny` below. A split pane is
measured as it is. CSS keyed on `data-density` shortens labels (`medium`), keeps icons and numbers (`narrow`: the
project name, the agent name, the reasoning, the percentage of the context and the word of the background tasks
go; the glyph of the tasks takes the place of their spinner, so the count never stands alone), and at `tiny` the
agent prompt leaves the Agent chip, whose model name is the part kept longest, and the sub-agents move into the `⋯`
menu, which then carries a mark. The Workspace chip drops the word **Worktree** before a worktree's name below
`wide`; the work place of a new session (`.workspace-chip-work-place`) keeps the words of **New worktree** down to
`narrow`, where **Project folder** is its icon, and is an icon at `tiny`. The Agent chip shows
`<provider logo> Provider / <model logo> Model`. A model without a logo of its own (no family in its id, `modelBrand`)
has its provider's, drawn once, outside the wrapping line, so it stays when the provider's name goes; otherwise the
provider's logo goes with its name. The provider is its display name (`providerName` of
`ProviderIcon.tsx`, from the provider brands of the configuration snapshot; an existing session falls back to the
`providerName` of its choices, then to the key), so two accounts of one provider type are told apart. The model part
is a `row-reverse` wrapping line one line high (`.composer-selection-model-line`): a provider without room wraps whole
to the hidden second line, never cut beside a cut model name, and leaves at `tiny`. In the surface it gives way after
the reasoning and the agent prompt (whose icon keeps its room at `narrow`); in the form of a new session the model part
shrinks first. The tooltip names the provider at every width. The tokens of the context, the cache
countdown, Stop and Send stay at every width. Container queries are not used: the layout containment they need
would make the surface the containing block of the fixed-position `⋯` menu, and its scroll box would cut it.

The `⋯` menu (`promptSurface/MoreMenu.tsx`) is a `details.composer-actions-menu` whose controls stay mounted while it
is closed: keyboard shortcuts (`invokeComposerControl` in `main.tsx`) open it, then click the control. It is
placed against the window, above its button, or below where there is more room. It holds what the owned composer
puts there (`OwnedSessionPanel.tsx`): **Session info**, **Reminders**, **Remote Control**, **Steer the running turn** and
**Enqueue for the next turn** while the session works, **Compact the conversation**, **Archive** (**Archive…** with
child sessions, whose question opens above the button; see **Archive** under "Workspace" in `doc/desktop.md`), **Expand prompt
editor**, and the providers (**N active providers**). The button carries a mark, with the count of active
reminders, when a reminder is active or Remote Control is on or failed.

`promptSurface/promptSurface.browser.test.ts` checks 1600, 1200, 900, 600 and 420 px in both themes, idle and
running, and a split view: one row above and one below the editor, nothing outside the surface, the Agent chip
clear of the meters, the provider named whole at 1200 px and wider, whole or gone (never cut) below, gone at `tiny`,
its logo and the model's shown with it and the model's alone without it (one logo for a model without its own);
then the worktree marker of a session, and the work place and the provider of a new session at every width.
`CODEALTA_SURFACE_SHOTS=<folder>` keeps a picture of each, and `agent-chip-<width>-<theme>.png` of the row of the chip.

## Short windows

The selected-session content container scrolls when a short or stacked workspace
cannot fit its contents. Its single mounted workspace has a 480px minimum height;
the usual full-height layout remains in effect when more room is available.
This reserves a usable layout for the header, timeline and regular composer
instead of shrinking the composer region to zero. It does **not** promise that
all those surfaces are visible simultaneously. Scroll the selected content pane
to reveal the editor or actions; keyboard focus also reveals reachable controls.
An already focused editor can require scrolling after a viewport resize.

This is a CSS policy on App-owned `.active-session-content` and its direct
`.session-workspace`, not a FlexLayout model change. Pane minima (including the
split view's 160px minima), public layout integration and component identities
are unchanged. There is no second workspace tree or scripted caret correction.
Local drafts, regular catalog/archived composers and owned composers use the
same scrolling content boundary. Existing inner overflow remains available for
large attachment/diagnostic sections or a manually minimized composer.

Manual composer height is still bounded within the workspace's layout area;
Home or Auto size resets it. In a short window that area may extend below the
visible content pane and is reached by scrolling. Resizing, scrolling and
language changes do not submit work, transfer drafts, replace original requests
or unlock uncertain operations.

Evidence: `tmp/short-composer-20260927/REPORT.md`. Earlier localization clipping
diagnostics remain under `tmp/localization-20260927/`; their short-window
limitation is addressed by this follow-up, not by enlarging the test viewport.
