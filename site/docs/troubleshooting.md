---
title: Troubleshooting
---

# Troubleshooting

## Open logs

Use `Ctrl+G Ctrl+L` or `/logs`. Logs are the first place to look for provider, credential, plugin and startup errors. CodeAlta also writes them to files:

- CodeAlta Desktop: `CodeAlta/desktop/logs` in the local application data folder, such as `%LOCALAPPDATA%\CodeAlta\desktop\logs` on Windows;
- CodeAlta TUI: `~/.alta/logs/`.

## The desktop app does not start

When the window of `alta` cannot open, CodeAlta says why and what to do: in the terminal you started it from, in a message box on Windows, or in a notification on Linux and macOS when you started it from the applications menu or the Dock. The log has the full report.

- **A library is missing (Linux).** The window needs GTK 4 and WebKitGTK 6.0; the message names the missing library. On Ubuntu 24.04 or later and Debian: `sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4`. Ubuntu 22.04 and older systems do not have WebKitGTK 6.0: use `altatui`.
- **No display (Linux).** Over SSH or in a console without an X11 or Wayland session, there is no window to open. Use `altatui`.
- **The window aborts at once on Ubuntu 24.04 or later.** WebKitGTK's sandbox needs unprivileged user namespaces, which Ubuntu restricts for programs without an AppArmor profile, such as `alta` installed with `dotnet tool`. The terminal says so. Choose one:
  - turn the sandbox off for CodeAlta only: `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1 alta`. The window loads only CodeAlta's local pages, but it is a sandbox turned off;
  - give `alta` an AppArmor profile that allows user namespaces, as Ubuntu does for its browsers; the message gives the path of the executable;
  - use `altatui`, which has no web view.
- **The WebView2 Runtime is missing (Windows).** Install or repair the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (Evergreen).
- **The Visual C++ runtime is missing (Windows).** Install the latest [Microsoft Visual C++ Redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist) for your processor.
- **The files of CodeAlta are missing or damaged.** Reinstall: `dotnet tool uninstall -g CodeAlta`, then `dotnet tool install -g CodeAlta`.

## Another instance is running

Only one CodeAlta runs on a profile at a time, desktop or TUI. It holds the lock file `~/.alta/alta.lock`.

> [!CAUTION]
> Do not delete the lock file of a running process: two instances would share sessions and state unsafely.

- A second `altatui` exits with the PID of the running instance.
- A second `alta` shows the window of the running desktop app; `alta .` or `alta <folder>` adds the folder as a project there.
- `alta --exit` asks the running desktop app to exit.

The desktop app can keep running in the notification area after its window is closed. If the TUI reports a running instance and you see no window, look for the CodeAlta icon in the notification area (on macOS, in the Dock), or run `alta --exit`.

## The window is busy

The window of CodeAlta Desktop waits for every answer of CodeAlta, so a very busy computer (a build, many agents, a wake from sleep) slows it down without making anything fail.

- **CodeAlta is busy**: CodeAlta has not answered for ten seconds. The notice says for how long and goes away once it answers; sessions, prompts and drafts are as they were.
- **This window lost its link to CodeAlta**: the window can no longer reach its host. **Reload** opens it again, with your tabs and drafts.
- **Closing**: while CodeAlta exits, the window says what it waits for. After ten seconds, **Force exit** ends CodeAlta at once, as the task manager would.

The log records what the window waited for and for how long, and each step of the exit: attach it to a report.

## Invalid `config.toml`

CodeAlta checks `~/.alta/config.toml` before it creates providers or sessions. When the file is invalid at startup, a recovery editor opens with the error marked; `Ctrl+S` saves and continues once the file is valid, `Ctrl+Q` exits.

- A file written by a newer CodeAlta should not be edited: update CodeAlta. When a newer version is published, the desktop recovery editor offers **Update and restart**.
- A provider whose `type` your version does not know does not make the file invalid: CodeAlta leaves that provider out, says so in **Settings > Providers** and in the log, and keeps its table as written.

Common fixes:

- keep table names unique;
- quote string values;
- write `enabled = true` or `enabled = false`, not quoted strings;
- put provider settings under `[providers.<provider-key>]`;
- set `[chat] default_provider` only to an enabled provider.

## Providers and sign-in

### No providers are enabled

Without an enabled provider, CodeAlta opens the provider setup: **Settings > Providers** in the desktop app, the Model Providers dialog in the TUI. Give the credentials, test the provider, then save; a successful test or sign-in enables it. See [Model Providers](model-providers/readme.md).

For an API-key provider, check that the environment variable exists where CodeAlta starts. Started from the Start Menu or the applications menu, the desktop app sees the variables of your user session, not those of a terminal.

### Sign-in is pending

- **CodeAlta Desktop**: use **Sign in with the browser** or **Sign in with a device code** on the provider in **Settings > Providers**.
- **CodeAlta TUI**: the Model Providers dialog shows the sign-in instructions while it waits. `Ctrl+G Ctrl+C` cancels; `Ctrl+G Ctrl+U` and `Ctrl+G Ctrl+D` copy the sign-in URL and the device code.
- ChatGPT signs in with the browser only; Copilot and xAI also take a device code.

For ChatGPT:

- Credentials of an older CodeAlta cannot be reused: sign in again, remove an old `auth_source`, and keep the `https://api.openai.com/v1` endpoint.
- The browser returns to `127.0.0.1` on the computer that runs CodeAlta; a remote computer needs a browser that reaches it.
- Signed in without plan access: choose **Continue with ChatGPT** to grant it, or use an API-key provider.
- `invalid_grant` at the first sign-in: choose **Continue with ChatGPT** again. `invalid_client` during model discovery: check the registration; the static model list does not fix a sign-in.
- A network failure keeps the credentials; a refresh token that no longer works needs another sign-in. When sign-out cannot confirm the revocation, disconnect CodeAlta in ChatGPT Settings.

See [Codex (ChatGPT)](model-providers/setup.md#codex-chatgpt).

## Claude Code

The `claude-code` provider runs the Claude Code CLI: the tested version once downloaded, else the one you installed. CodeAlta has no sign-in of its own for it. See [Claude Code](model-providers/setup.md#claude-code).

### Install and sign-in

- **Claude Code was not found**: in CodeAlta Desktop, choose **Download** in the **Claude Code CLI** box of the provider, or set `command` to the path of the executable. The desktop app started from the Dock or the Start menu does not have the `PATH` of your shell; CodeAlta also looks in `~/.local/bin`, Homebrew and npm folders. On Windows, the `claude.cmd` of npm is not run: download the tested version, or install the native Claude Code (`irm https://claude.ai/install.ps1 | iex`).
- **The download fails**: the box says why. **Download** again goes on where it stopped. "Another CodeAlta is downloading Claude Code" means the other instance (normal or developer) holds the download: wait for it. A download that does not match the tested version is removed: download again.
- **Test says this Claude Code is another version**: CodeAlta runs the Claude Code you installed. It usually works; download the tested version to run that one.
- **Claude Code is not signed in**: in CodeAlta Desktop, run the command of the **Claude Code CLI** box in a terminal, type `/login`, then **Test selected provider**. In CodeAlta TUI, run `claude` and `/login`, then **Refresh**. A provider with an **Account folder** is signed in in that folder only. `claude auth status`, run the same way, shows the sign-in; the Claude desktop application does not sign in the CLI.
- **A second Claude provider has none of my settings**: its account folder starts empty. Copy what you want from `~/.claude`.
- **A usage limit or billing message**: it comes from Claude Code and its account. A message about the limits of an API *workspace* means Claude Code used `ANTHROPIC_API_KEY`, not your login.
- **A session says `ANTHROPIC_API_KEY is set`**: Claude Code has no saved answer for that key. Choose **ANTHROPIC_API_KEY** in the provider editor, or set `anthropic_api_key = "use"` or `"ignore"`. With `ignore`, Claude Code uses your login. Removing the key from your user environment works too.

### Conversations elsewhere

- **A resumed session starts without its context**: the transcript is under `projects` of the account folder, so changing the **Account folder** leaves it behind. CodeAlta then starts a new conversation with the recorded history.
- **"Claude Code did not start in time to resume the conversation"**: the conversation is kept. Send the message again.
- **The card "This Claude Code conversation is open in VS Code"**: another Claude process has it open, VS Code or `claude` in a terminal. Close it there and choose **Wait until it is free**, continue a copy, or open it here anyway. See [A conversation open elsewhere](sessions/claude-code.md#a-conversation-open-elsewhere). In VS Code, the Claude Code panel keeps the conversation open while it is shown.
- **"The Claude Code conversation … is open in …"** or **"Claude Code refused to resume the conversation …"**: the run had nobody to ask (a compaction, Remote Control connecting, or a background session of Claude Code still running it). Close the conversation in the other program, or wait for the background session to end, then send again.
- **"Not sent: the conversation changed while waiting"**: turns were added in the other program while the prompt waited. They are added to the session: send again.
- **"This conversation was changed outside CodeAlta in a way it cannot follow"**: it was rewound, or resumed at an earlier point, elsewhere. Claude Code goes on from the whole conversation; only CodeAlta's timeline misses what changed there.
- **VS Code does not list a session of CodeAlta**: VS Code leaves out the conversations run through the Claude Code SDK. Use **Open in VS Code** in the menu of the session.
- **Open in VS Code refers to its own account folder**: the provider has an **Account folder** of its own. Use **Run in terminal** or **Copy resume command**, which set `CLAUDE_CONFIG_DIR`. See [Open a session in VS Code or a terminal](sessions/claude-code.md#open-in-vs-code).
- **VS Code says the conversation is open in another Claude process**: CodeAlta keeps Claude Code running up to ten minutes after a turn. **Open in VS Code**, **Run in terminal** and **Copy resume command** close it first; when **Copy resume command** says CodeAlta still has it open, do what its message says, then copy again.

## Sessions

### A prompt did not send

CodeAlta keeps the text of a prompt that could not be sent. Check that:

- the provider is enabled and ready, and in the [space](spaces.md#providers-of-a-space) of the session;
- its credentials are still valid, and the model exists for it;
- the session is not waiting for a sign-in or for the provider to start;
- the logs show no provider error.

A busy session puts the prompt in its waiting list instead of sending it.

### A provider or tool is missing

- **The session sends nothing and the provider chooser opens**: its provider was taken out of the session's space. Choose a provider of the space, or give the provider that space again (**Settings > Providers**, the chip of its row).
- **"… is no longer available: choose another provider to send"**: the provider was deleted or turned off. Choose another one in the configuration of the session, or turn it on again.
- **An agent says `server_not_in_space`, or does not see a skill or an agent prompt**: the resource is not in the session's space. See [What a space has](spaces.md#what-a-space-has).
- **The Default space no longer shows some projects or chats**: they belong to another space. **Show what belongs to other spaces** in **Settings > Spaces** shows everything there.
- **An agent has no `shell_command`**: it is turned off in **Settings > Permissions > Tools**, in the **Tools** of the space, or in the agent prompt. See [Tools turned off](workspace/settings.md#tools-turned-off).
- **Commits have the wrong author**: open the space in **Settings > Spaces**, then **Identity**, and **Check** on the Git card. A running session keeps the identity it started with, and a repository's own `.git/config` comes first. See [Identity](spaces.md#identity).
- **A session did not fall back after a quota error**: the fallback is off until you turn it on, and uses only the models of the session's tier whose provider is in its space. See [Model tiers and fallback](model-tiers.md#fallback-when-a-provider-fails).

### History cannot be read

When the desktop app cannot read a page of a session's history, the session file is left untouched. The desktop [logs](#open-logs) have a `CodeAlta.Desktop.History` warning with the session id and the error: include it in your report.

### Context is too large

Open the context usage with `Ctrl+G Ctrl+U`. Press `Ctrl+F11` to compact an idle session. If compaction keeps missing its target, check the size of attached files and the [context limits of the model](model-providers/configuration.md#model-metadata-overrides).

### A sandboxed session runs nothing

A session whose [sandbox](sessions/sandbox.md) cannot run runs no command, and says why. **Settings > Sandbox > Check** shows the step that fails:

- **Linux**: install `bubblewrap`, and `slirp4netns` for a profile with a network.
- **The profile cannot be used**: a profile was removed or renamed while a provider, a space or a project still names it. Choose another one there.
- **A host is refused**: the profile does not list it. Add it to the hosts of a profile of yours.
- **The provider runs its own tools** and the sandbox cannot start it: choose another provider for the session, or turn its sandbox off.
- **An ACP agent is not signed in, in a sandbox**: it has a home folder of its own there, without your sign-in. Give its provider a token or an API key in its **Environment** (Settings > Providers). An agent that reaches no host of its service needs them in **Hosts in a sandbox**.
- **Claude Code is not signed in, in a sandbox on macOS**: its sign-in is in the Keychain, out of reach. Set `CLAUDE_CODE_OAUTH_TOKEN` with a token from `claude setup-token`, or use an API key.
- **Commands start in `C:\` instead of the project (Windows)**: a folder above the project holds tens of thousands of entries, as the temporary folder does, and the sandbox keeps such a folder out of reach. Move the project elsewhere.
- **`dotnet build` fails without an error (Windows)**: MSBuild cannot start its worker nodes in the sandbox. Build with `-m:1`; the agent is told to.
- **`dotnet test` stops after 90 seconds with a profile that reaches hosts (Windows)**: before Windows 11 build .9550, the processes of such a sandbox cannot connect to each other, as VSTest needs. Let the agent run the tests outside the sandbox, or use a profile without network once the packages are restored.

## A plugin is broken

> [!WARNING]
> A broken plugin can fail when it is found, built, loaded or run. If CodeAlta cannot start, start it without plugins first.

- CodeAlta TUI: `altatui --no-plugins`, or `altatui --plugin-safe-mode`.
- Both apps: set `CODEALTA_DISABLE_PLUGINS=1` before starting CodeAlta. This is the way for the desktop app, which has no such options.

Then turn off the failing plugin in plugin management, or read the [logs](#open-logs) for its build and load errors.

**Plugin build says `plugin.cs` was treated as a project file**: source plugins need a .NET SDK that builds single C# files. Install a supported SDK, restore the files CodeAlta generated in the plugin folder if they were deleted, or start without plugins and fix the plugin.

## The microphone

[Speech to prompt](speech.md) is a feature of CodeAlta Desktop.

- **No microphone button beside Send**: speech to prompt is turned off in **Settings > Personalization > Speech**, or the system gives the window no microphone support.
- **It records but writes nothing**: CodeAlta listens to a device that hears nothing, often a virtual microphone or one that is unplugged. In **Settings > Personalization > Speech**, choose your microphone and press **Test**: the bar must move when you speak.
- **"The microphone is not available"**: the system refuses it. On Windows, turn on **Settings > Privacy & security > Microphone > Let desktop apps access your microphone**. On macOS, allow CodeAlta (or the terminal it was started from) in **System Settings > Privacy & Security > Microphone**.
- **The download fails**: the model comes from `huggingface.co` and the speech engine from `api.nuget.org`; both must be reachable. Start the download again: it goes on where it stopped.
- **"The speech model could not be loaded"**: the engine refused the model, as with a custom model it does not support. Choose another model and delete this one on the Speech page. The log has what the engine said.

## The TUI in a terminal

### Glyphs look wrong

CodeAlta TUI uses Nerd Fonts icons for tree expanders, status marks and other symbols. Boxes, question marks or wrong icons mean the terminal is not using a current Nerd Font.

1. Install the latest version of your font from [Nerd Fonts](https://www.nerdfonts.com/font-downloads). Version 3 moved many icons, so a v2-era Nerd Font shows wrong symbols.
2. Remove older copies of the same font first: with both installed, the terminal can keep the old one.
3. Select the font in the terminal profile that runs `altatui`, such as `CaskaydiaCove Nerd Font`; an editor font does not count.
4. Close every terminal window and start a new one. On Linux, refresh the font cache with `fc-cache -f -v` if needed.

Old copies can hide in Windows **Settings > Personalization > Fonts**, `%LOCALAPPDATA%\Microsoft\Windows\Fonts`, `C:\Windows\Fonts`, macOS **Font Book**, and `~/.local/share/fonts` or `~/.fonts` on Linux. If only some icons are wrong, check that the profile does not fall back to another font.

### Windows Terminal shortcuts

Windows Terminal can keep shortcuts for its own paste, pane and navigation actions. CodeAlta switches tabs with `Ctrl+Alt+Left` and `Ctrl+Alt+Right`, so Windows Terminal keeps `Alt+Left` and `Alt+Right` for its panes. When `Ctrl+V` or another shortcut does not reach CodeAlta, unbind it in the `keybindings` array of Windows Terminal's `settings.json`:

```json
{ "keys": "ctrl+v", "id": "unbound" }
```

### Windows Terminal is slow

After CodeAlta TUI has run in the same Windows Terminal tab for many hours, the terminal can become sluggish, even after restarting `altatui` in that tab. Open a new tab or window and start `altatui` there.
