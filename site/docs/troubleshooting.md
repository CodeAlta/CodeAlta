---
title: Troubleshooting
---

# Troubleshooting

## Open logs

Use `Ctrl+G Ctrl+L` or `/logs`. CodeAlta also writes rolling diagnostic logs. The TUI writes them under:

```text
~/.alta/logs/
```

The desktop app writes them under `CodeAlta/desktop/logs` in the local application data folder, for example `%LOCALAPPDATA%\CodeAlta\desktop\logs` on Windows.

Logs are the first place to check for provider startup, credential, plugin build, and runtime errors.

## A session's history cannot be read

When the desktop app shows that a history page could not be read, the session file itself is left untouched. The desktop [logs](#open-logs) contain a `CodeAlta.Desktop.History` warning with the session id and the underlying error; include it when you report the problem.

## Glyphs or tree icons look wrong

This applies to the TUI only. CodeAlta's terminal UI uses Nerd Fonts icons for tree expanders, status indicators, and other compact symbols. If these appear as empty boxes, question marks, unrelated icons, or misaligned glyphs, the terminal is usually not using a current Nerd Font-compatible font.

Check the font setup in this order:

1. Install the latest version of your preferred font from [Nerd Fonts](https://www.nerdfonts.com/font-downloads). Nerd Fonts v3.0.0 included breaking glyph code-point changes, so an older v2-era Nerd Font can be installed and still display the wrong symbols.
2. Uninstall or delete older copies of the same patched font before reinstalling. Having both old and new variants installed can make the terminal keep selecting the outdated font.
3. Select the updated Nerd Font in the terminal profile that runs `altatui`; changing an editor font does not affect CodeAlta. For example, `CaskaydiaCove Nerd Font` is expected to work when the installed font files are current.
4. Close all terminal windows and start a fresh terminal before launching `altatui` again. On Linux, refresh the font cache if needed with `fc-cache -f -v`.

Platform-specific places to check for stale font copies include Windows **Settings > Personalization > Fonts**, `%LOCALAPPDATA%\Microsoft\Windows\Fonts`, `C:\Windows\Fonts`, macOS **Font Book**, and Linux user font directories such as `~/.local/share/fonts` or `~/.fonts`.

If only some icons are wrong after updating, verify that the active terminal profile is not falling back to a different font family.

## Windows Terminal shortcuts do not reach CodeAlta

Windows Terminal can reserve shortcuts for its own paste, pane, and navigation actions before CodeAlta can receive them. CodeAlta uses `Ctrl+Alt+Left` and `Ctrl+Alt+Right` for tab switching so Windows Terminal's default `Alt+Left` and `Alt+Right` pane switching can remain enabled. If `Ctrl+V` or another CodeAlta shortcut does not behave as expected, review your Windows Terminal settings and unbind only the terminal-level shortcuts that CodeAlta needs to receive directly.

Add entries like this to the `keybindings` array in Windows Terminal's `settings.json` when a key is currently assigned to a terminal action:

```json
{ "keys": "ctrl+v", "id": "unbound" }
```

## Windows Terminal feels slow after a long session

> [!NOTE]
> A small known issue can affect Windows Terminal after CodeAlta has been running in the same tab for many hours, such as a full day. The UI may begin to feel sluggish. Restarting `altatui` in the same Windows Terminal tab may not restore normal responsiveness, but opening a new tab or window and launching `altatui` there usually does.

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

If no provider is enabled, CodeAlta opens the provider setup automatically: the Model Providers dialog in the TUI, or **Settings > Providers** in the desktop app. Configure credentials, test the provider, then save. A successful test enables the provider automatically; Codex/Copilot browser or device login also enables its provider automatically.

For API-key providers, verify that the environment variable exists in the shell that launches CodeAlta. When the desktop app is started from the Start Menu or the applications menu, it sees the environment variables of your user session, not those of a terminal.

## Claude Code is not found or not signed in

The `claude-code` provider runs the Claude Code CLI you installed; CodeAlta has no sign-in for it.

- **Claude Code was not found**: install it, or set `command` of the provider to the path of the executable. The desktop app started from the Dock or the Start menu does not have the `PATH` of your shell; CodeAlta also looks in `~/.local/bin`, Homebrew and npm folders. On Windows, install the native Claude Code (`irm https://claude.ai/install.ps1 | iex`): the `claude.cmd` of an npm installation is not run.
- **Claude Code is not signed in**: run `claude` in a terminal, use `/login`, then use **Refresh** in the provider editor. `claude auth status` shows what the CLI is signed in with.
- **A turn fails with a usage limit or a billing message**: the message comes from Claude Code and the account it uses. Limits are those of that account's plan.
- **A resumed session starts without its context**: Claude Code keeps its transcript per folder under `~/.claude/projects`. When it is missing, CodeAlta starts a new Claude Code conversation and gives it the recorded conversation as context.

## Codex or Copilot login is pending

In the TUI, the Model Providers dialog keeps browser sign-in and supported device-login instructions visible while authorization is pending. ChatGPT uses **Continue with ChatGPT** (browser only); Copilot and xAI also support device login. The current operation can be canceled from the dialog or with `Ctrl+G Ctrl+C`. Use `Ctrl+G Ctrl+U` / `Ctrl+G Ctrl+D` to copy the current login URL or device code. In the desktop app, use **Sign in with the browser** or **Sign in with a device code** on the provider in **Settings > Providers**.

For ChatGPT, legacy Codex credentials cannot be reused or imported. Sign in again, remove old credential-import `auth_source` settings, and use the public `https://api.openai.com/v1` endpoint. The callback must reach `127.0.0.1` on the machine running CodeAlta; remote/headless setups need browser access to that loopback listener. If sign-in succeeds without plan permission, choose **Continue with ChatGPT** to enable plan access or configure an API-key provider. Temporary refresh/network failures retain credentials; an unusable refresh token requires another sign-in with the saved registration. If sign-out reports unconfirmed revocation, disconnect CodeAlta in ChatGPT Settings.

If the first authorization-code exchange reports `invalid_grant`, choose **Continue with ChatGPT** again to get a fresh code using the retained registration ID. This incomplete registration cannot run model requests until identity validation succeeds. If discovery reports `invalid_client`, check the issued client registration rather than expecting static model fallback to repair authentication.

## A plugin is broken

> [!WARNING]
> Broken plugins can fail during discovery, build, load, activation, or callbacks. Use safe mode or `--no-plugins` first if CodeAlta cannot start normally.

Start the TUI without dynamic plugins:

```sh
altatui --no-plugins
```

Or with plugin safe mode:

```sh
altatui --plugin-safe-mode
```

For both apps, you can also set this environment variable before starting CodeAlta. The desktop app does not have the two options above, so this is the way to start it without plugins:

```sh
CODEALTA_DISABLE_PLUGINS=1
```

Then open plugin management, disable the failing plugin, or inspect the [logs](#open-logs) for build/load diagnostics.

## Plugin build says `plugin.cs` was treated as a project file

Source plugins require a .NET SDK that supports native file-based C# builds. Restore CodeAlta-generated plugin-root files if they were deleted, install the supported SDK, or start with `--plugin-safe-mode` / `--no-plugins` and fix the plugin package.

## Another CodeAlta instance is already running

Only one CodeAlta instance can run on a profile at a time, desktop or TUI. CodeAlta uses:

> [!CAUTION]
> Do not delete the lock file for a running process. Multiple active instances would share user state, sessions, and provider/runtime files unsafely.

```text
~/.alta/alta.lock
```

A second `altatui` exits with the PID of the already-running instance because multiple instances would share session state unsafely. A second `alta` shows the window of the running desktop app instead. `alta --exit` asks the running desktop app to exit.

The desktop app can keep running in the notification area after its window is closed. If the TUI reports a running instance and you see no window, look for the CodeAlta icon in the notification area, or run `alta --exit`.

## A prompt did not send

CodeAlta preserves prompt text when dispatch fails. Check:

- the selected provider is enabled and ready;
- credentials are still valid;
- the selected model exists for that provider;
- the session is not waiting for a login or provider startup operation;
- logs for provider-specific failures.

If the session is busy, the prompt may be in the waiting list rather than sent immediately.

## Context is too large

Open the context usage popup with `Ctrl+G Ctrl+U`. For idle started sessions, press `Ctrl+F11` to run manual compaction. If compaction repeatedly misses its target, review attached file sizes and provider context metadata in the provider configuration.
