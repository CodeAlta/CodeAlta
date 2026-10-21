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

If the file was written by a newer CodeAlta than the one you started, do not edit it: update instead. When a newer version is published, the desktop recovery editor says so above the file and has **Update and restart**.

A provider whose `type` your version does not know does not make the file invalid. CodeAlta leaves that provider out, says so in Settings → Providers and in the application log, and keeps its section in the file as written, so a newer version can still use it. Versions up to 1.3.0 refuse the whole file instead.

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

The `claude-code` provider runs the Claude Code CLI: the version CodeAlta is tested with once you downloaded it, else the one you installed. CodeAlta has no sign-in for it.

- **Claude Code was not found**: in CodeAlta Desktop, select the provider in **Settings > Providers** and choose **Download** in its **Claude Code CLI** box. You can also install Claude Code yourself, or set `command` of the provider to the path of the executable. The desktop app started from the Dock or the Start menu does not have the `PATH` of your shell; CodeAlta also looks in `~/.local/bin`, Homebrew and npm folders. On Windows the `claude.cmd` of an npm installation is not run: download the tested version, or install the native Claude Code (`irm https://claude.ai/install.ps1 | iex`).
- **The download of Claude Code fails**: the box says why. A network failure goes on where it stopped when you choose **Download** again. "Another CodeAlta is downloading Claude Code" means the other instance (the normal or the developer one) holds the download: wait for it. A download that does not match the tested version is removed; download it again.
- **Test says this Claude Code is another version**: CodeAlta runs the Claude Code you installed, and is tested with another version. It usually works; download the tested version to run that one instead.
- **Claude Code is not signed in**: the test of the provider says so. Copy the command of the **Claude Code CLI** box in CodeAlta Desktop, run it in a terminal, type `/login`, then use **Test selected provider**; in CodeAlta TUI, run `claude` and `/login`, then **Refresh** in the provider editor. A provider with an **Account folder** is signed in in that folder only: the command of its box starts Claude Code with it. `claude auth status`, run the same way, shows what the CLI is signed in with. Being signed in to the Claude desktop application does not sign in the CLI.
- **A second Claude provider has none of my settings, memory or MCP servers**: its account folder starts empty. Copy what you want from your usual folder (`~/.claude`) into it.
- **A turn fails with a usage limit or a billing message**: the message comes from Claude Code and the account it uses. Limits are those of that account's plan. A message about the usage limits of an API *workspace* means Claude Code used an API key (`ANTHROPIC_API_KEY`), not your login: see the next point.
- **A session says `ANTHROPIC_API_KEY is set`**: the variable is set, and Claude Code has no answer saved for that key. Choose whether Claude Code uses it: **ANTHROPIC_API_KEY** in the provider editor of CodeAlta Desktop, or `anthropic_api_key = "use"` or `"ignore"` in the configuration. With `ignore`, Claude Code signs in with your login. If the key is only meant for other tools, removing it from your user environment also works.
- **A resumed session starts without its context**: Claude Code keeps its transcript per folder under `projects` of the account folder (`~/.claude/projects` by default), so changing the **Account folder** of a provider leaves its sessions' transcripts behind. When it is missing, CodeAlta starts a new Claude Code conversation and gives it the recorded conversation as context.
- **A turn fails with "Claude Code did not start in time to resume the conversation"**: Claude Code was slow to start, but the conversation is kept. Send the message again: CodeAlta no longer starts a new conversation when Claude Code is slow to start.
- **The card "This Claude Code conversation is open in VS Code" appears**: another Claude process of this computer has the conversation open, VS Code or `claude` in a terminal. Close it there and choose **Wait until it is free**, or continue a copy, or open it here anyway. See [A conversation open elsewhere](sessions.md#a-conversation-open-elsewhere). In VS Code, the Claude Code panel of that conversation keeps it open while it is shown.
- **A turn fails with "The Claude Code conversation … is open in …"** or **"Claude Code refused to resume the conversation …: a background session of Claude Code is running it"**: the run had nobody to ask what to do (a compaction, Remote Control that connects, or a background session of Claude Code that still runs it). Close the conversation in the other program, or wait for the background session to end, then send the message again.
- **The prompt came back with "Not sent: the conversation changed while waiting"**: turns were added to the conversation in the other program while the prompt waited for it. They are added to the session before your next message: send it again.
- **The timeline says "This conversation was changed outside CodeAlta in a way it cannot follow"**: the conversation was rewound, or resumed at an earlier point, in VS Code or a terminal. Claude Code has the whole conversation and goes on from it; the timeline of CodeAlta misses only what changed there.
- **VS Code does not list a session of CodeAlta**: the session list of VS Code leaves out the conversations that programs run through the Claude Code SDK, as CodeAlta runs Claude Code, and has no setting to show them. Use **Open in VS Code** in the menu of the session. A conversation started in VS Code stays listed there after CodeAlta continued it.
- **Open in VS Code says that VS Code opens conversations from its own account folder**: the provider of the session has an **Account folder** of its own, where the conversation is. Use **Run in terminal** or **Copy resume command**, which set `CLAUDE_CONFIG_DIR` to that folder. See [Open a session in VS Code or a terminal](sessions.md#open-a-session-in-vs-code-or-a-terminal).
- **VS Code says the conversation is open in another Claude process**: CodeAlta keeps Claude Code running up to ten minutes after a turn. **Open in VS Code**, **Run in terminal** and **Copy resume command** in the menu of the session close it first; when **Copy resume command** says that CodeAlta still has the conversation open, do what its message says, then copy the command again.

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

## The desktop app does not start

When the window of `alta` cannot open, CodeAlta says why and what to do: in the terminal you started `alta` from, in a message box on Windows, or in a notification on Linux and macOS when you started it from the applications menu or the Dock. The log has the full report: see [Open logs](#open-logs).

- **A library is missing (Linux).** The desktop app draws its window with GTK 4 and WebKitGTK 6.0. The message names the missing library. On Ubuntu 24.04 or later and Debian, install them with `sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4`. Ubuntu 22.04 and older systems do not have WebKitGTK 6.0: use the terminal app, `altatui`.
- **No display (Linux).** Over SSH or in a console without an X11 or Wayland session, the desktop app cannot open a window. Use `altatui`.
- **The window aborts at once on Ubuntu 24.04 or later.** WebKitGTK runs its pages in a sandbox that needs unprivileged user namespaces, which Ubuntu restricts for programs without an AppArmor profile, such as `alta` installed with `dotnet tool`. The terminal you started `alta` from says so. Choose one of these:
  - Turn the sandbox off for CodeAlta only: `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1 alta`. The window loads only the local pages of CodeAlta, but it is a sandbox turned off.
  - Give CodeAlta an AppArmor profile that allows user namespaces, as Ubuntu does for its browsers. The message gives the path of the `alta` executable to put in it.
  - Use the terminal app, `altatui`, which has no web view.
- **The WebView2 Runtime is missing (Windows).** Install or repair the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (Evergreen). It is part of Windows 11 and of an up-to-date Windows 10.
- **The Visual C++ runtime is missing (Windows).** Install the latest [Microsoft Visual C++ Redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist) for your processor.
- **The files of CodeAlta are missing or damaged.** Reinstall it: `dotnet tool uninstall -g CodeAlta`, then `dotnet tool install -g CodeAlta`.

## Another CodeAlta instance is already running

Only one CodeAlta instance can run on a profile at a time, desktop or TUI. CodeAlta uses:

> [!CAUTION]
> Do not delete the lock file for a running process. Multiple active instances would share user state, sessions, and provider/runtime files unsafely.

```text
~/.alta/alta.lock
```

A second `altatui` exits with the PID of the already-running instance because multiple instances would share session state unsafely. A second `alta` shows the window of the running desktop app instead, and `alta .` or `alta <folder>` adds the folder as a project of the running app. `alta --exit` asks the running desktop app to exit.

The desktop app can keep running in the notification area after its window is closed. If the TUI reports a running instance and you see no window, look for the CodeAlta icon in the notification area (on macOS, in the Dock), or run `alta --exit`.

## The window says CodeAlta is busy, lost its link or is closing

The window of CodeAlta Desktop waits for every answer of CodeAlta: nothing it asks is given up after a delay, so a computer that is very busy (a build, many agents, a machine that was asleep) slows the window down without making anything fail. When CodeAlta has not answered for ten seconds, the window says **CodeAlta is busy** and for how long; the notice goes away by itself once it answers, and your sessions, prompts and drafts are as they were.

**This window lost its link to CodeAlta** says the window can no longer reach its host: **Reload** opens it again, with your tabs and drafts.

While CodeAlta exits, it closes its sessions, terminals and services one after the other, and the window says what it waits for. When that lasts more than ten seconds, **Force exit** ends CodeAlta at once, as ending it from the task manager would. The log (`%LOCALAPPDATA%\CodeAlta\desktop\logs` on Windows) records what the window waited for and how long, the moments CodeAlta did not run, each step of the exit, and every failure of the host: attach it to a report.

## A prompt did not send

CodeAlta preserves prompt text when dispatch fails. Check:

- the selected provider is enabled and ready;
- the provider is in the [space](spaces.md#providers-of-a-space) of the session;
- credentials are still valid;
- the selected model exists for that provider;
- the session is not waiting for a login or provider startup operation;
- logs for provider-specific failures.

If the session is busy, the prompt may be in the waiting list rather than sent immediately.

## A session cannot use a provider, a server or a tool

- **The session sends nothing and the provider chooser opens**: its provider was taken out of the session's space. Choose a provider of the space, or give the provider that space again (**Settings > Providers**, the chip of its row).
- **An agent says `server_not_in_space`, or does not see a skill or an agent prompt**: the resource is not in the session's space. See [What a space has](spaces.md#what-a-space-has).
- **The Default space no longer shows some projects or chats**: they belong to another space. Turn on **Show what belongs to other spaces** in **Settings > Spaces** to see everything there.
- **An agent has no `shell_command`**: it is turned off in **Settings > Permissions > Tools**, in the **Tools** section of the space, or in the agent prompt. See [Tools turned off](workspace.md#tools-turned-off).
- **Commits have the wrong author**: open the space in **Settings > Spaces**, then **Identity**, and **Check** on the Git card. A running session keeps the identity it started with, and the settings of a repository in its own `.git/config` come first. See [Identity](spaces.md#identity).
- **A session did not fall back after a quota error**: the fallback is off until you turn it on (**Settings > Models > Model tiers**, or the **New sessions** section of a space with tiers of its own). It uses only the models of the session's tier whose provider is in its space, and a session on a model of no tier does not fall back. See [Model tiers and fallback](model-tiers.md#fallback-when-a-provider-fails).

## The microphone button is missing or hears nothing

[Speech to prompt]({{site.basepath}}/docs/speech/) is a feature of CodeAlta Desktop.

- **No microphone button beside Send**: speech to prompt is turned off in **Settings > Personalization > Speech**, or the system gives the window no microphone support.
- **It records but nothing is written**: CodeAlta is listening to a device that hears nothing, often the default device of the system when it is a virtual microphone or one that is not plugged in. Open **Settings > Personalization > Speech**, choose your microphone and press **Test**: the bar must move when you speak.
- **"The microphone is not available"**: the system refuses the microphone. On Windows, turn on **Settings > Privacy & security > Microphone > Let desktop apps access your microphone**. On macOS, allow CodeAlta (or the terminal it was started from) in **System Settings > Privacy & Security > Microphone**. Check also that a microphone is plugged in and is the input device of the system.
- **The download fails**: the model comes from `huggingface.co` and the speech engine from `api.nuget.org`. Both must be reachable. Start the download again: it goes on from where it stopped.
- **"The speech model could not be loaded"**: the engine refused the model, which happens with a custom model it does not support. Choose another model, and delete this one on the Speech page. The application log has what the engine said.

## Context is too large

Open the context usage popup with `Ctrl+G Ctrl+U`. For idle started sessions, press `Ctrl+F11` to run manual compaction. If compaction repeatedly misses its target, review attached file sizes and provider context metadata in the provider configuration.
