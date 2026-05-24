---
title: Troubleshooting
---

# Troubleshooting

## Open logs

Use `Ctrl+G Ctrl+L`, `/logs`, or the **Show Logs** button in the navigator footer. CodeAlta also writes rolling diagnostic logs under:

```text
~/.alta/logs/
```

Logs are the first place to check for provider startup, credential, plugin build, and runtime errors.

## Glyphs or tree icons look wrong

CodeAlta's terminal UI uses Nerd Fonts icons for tree expanders, status indicators, and other compact symbols. If these appear as empty boxes, question marks, unrelated icons, or misaligned glyphs, the terminal is usually not using a current Nerd Font-compatible font.

Check the font setup in this order:

1. Install the latest version of your preferred font from [Nerd Fonts](https://www.nerdfonts.com/font-downloads). Nerd Fonts v3.0.0 included breaking glyph code-point changes, so an older v2-era Nerd Font can be installed and still display the wrong symbols.
2. Uninstall or delete older copies of the same patched font before reinstalling. Having both old and new variants installed can make the terminal keep selecting the outdated font.
3. Select the updated Nerd Font in the terminal profile that runs `alta`; changing an editor font does not affect CodeAlta. For example, `CaskaydiaCove Nerd Font` is expected to work when the installed font files are current.
4. Close all terminal windows and start a fresh terminal before launching `alta` again. On Linux, refresh the font cache if needed with `fc-cache -f -v`.

Platform-specific places to check for stale font copies include Windows **Settings > Personalization > Fonts**, `%LOCALAPPDATA%\Microsoft\Windows\Fonts`, `C:\Windows\Fonts`, macOS **Font Book**, and Linux user font directories such as `~/.local/share/fonts` or `~/.fonts`.

If only some icons are wrong after updating, verify that the active terminal profile is not falling back to a different font family.

## Windows Terminal shortcuts do not reach CodeAlta

Windows Terminal can reserve shortcuts for its own paste, pane, and navigation actions before CodeAlta can receive them. If `Ctrl+V`, `Alt+Left`, `Alt+Right`, or another CodeAlta shortcut does not behave as expected, review your Windows Terminal settings and unbind terminal-level shortcuts that CodeAlta needs to receive directly.

Add entries like these to the `keybindings` array in Windows Terminal's `settings.json` when those keys are currently assigned to terminal actions:

```json
{ "keys": "ctrl+v", "id": "unbound" },
{ "keys": "alt+left", "id": "unbound" },
{ "keys": "alt+right", "id": "unbound" }
```

## Windows Terminal feels slow after a long session

> [!NOTE]
> A small known issue can affect Windows Terminal after CodeAlta has been running in the same tab for many hours, such as a full day. The UI may begin to feel sluggish. Restarting `alta` in the same Windows Terminal tab may not restore normal responsiveness, but opening a new tab or window and launching `alta` there usually does.

This appears to be related to how Windows Terminal handles long-running, high-refresh terminal rendering. CodeAlta uses XenoAtom.Terminal.UI for an interactive interface that can render at up to 60 FPS. If you notice slowdown after a long session, move to a fresh Windows Terminal tab or window.

## Invalid `config.toml`

CodeAlta validates `~/.alta/config.toml` before creating providers or sessions. If the file is invalid at startup, CodeAlta opens a TOML recovery editor with syntax highlighting, an error marker, live parse feedback, `Ctrl+S` Save and Continue when valid, and `Ctrl+Q` Exit.

> [!IMPORTANT]
> Fix configuration parse errors before starting agent work. Provider creation, project overrides, and plugin configuration depend on a valid TOML file.

Common fixes:

- keep table names unique;
- quote string values;
- use `enabled = true` or `enabled = false` booleans, not quoted strings;
- put provider settings under `[providers.<provider-key>]`;
- use `[chat] default_provider = "<provider-key>"` only for an enabled provider.

## No providers are enabled

If no provider is enabled, CodeAlta opens the Model Providers dialog automatically. Configure credentials, click **Test Provider**, then **Save**. A successful test enables the provider automatically; Codex/Copilot browser or device login also enables its provider automatically.

For API-key providers, verify that the environment variable exists in the shell that launches `alta`.

## Codex or Copilot login is pending

The Model Providers dialog keeps Browser Login and Device Login instructions visible while authorization is pending. The current operation can be canceled from the dialog or with `Ctrl+G Ctrl+C`. Use `Ctrl+G Ctrl+U` / `Ctrl+G Ctrl+D` to copy the current login URL or device code.

## A plugin is broken

> [!WARNING]
> Broken plugins can fail during discovery, build, load, activation, or callbacks. Use safe mode or `--no-plugins` first if CodeAlta cannot start normally.

Start CodeAlta without dynamic plugins:

```sh
alta --no-plugins
```

Or with plugin safe mode:

```sh
alta --plugin-safe-mode
```

You can also set:

```sh
CODEALTA_DISABLE_PLUGINS=1
```

Then open plugin management, disable the failing plugin, or inspect `~/.alta/logs/codealta.log` for build/load diagnostics.

## Plugin build says `plugin.cs` was treated as a project file

Source plugins require a .NET SDK that supports native file-based C# builds. Restore CodeAlta-generated plugin-root files if they were deleted, install the supported SDK, or start with `--plugin-safe-mode` / `--no-plugins` and fix the plugin package.

## Another CodeAlta instance is already running

Only one `alta` application instance can run on a machine at a time. CodeAlta uses:

> [!CAUTION]
> Do not delete the lock file for a running process. Multiple active instances would share user state, sessions, and provider/runtime files unsafely.

```text
~/.alta/alta.lock
```

A second launch exits with the PID of the already-running instance because multiple instances would share thread/session state unsafely.

## A prompt did not send

CodeAlta preserves prompt text when dispatch fails. Check:

- the selected provider is enabled and ready;
- credentials are still valid;
- the selected model exists for that provider;
- the thread is not waiting for a login or provider startup operation;
- logs for provider-specific failures.

If the thread is busy, the prompt may be in the waiting list rather than sent immediately.

## Context is too large

Open the context usage popup with `Ctrl+G Ctrl+U`. For idle started threads, press `F11` to run manual compaction. If compaction repeatedly misses its target, review attached file sizes and provider context metadata in the provider configuration.
