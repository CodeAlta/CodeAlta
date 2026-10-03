# Short-window composer layout

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
