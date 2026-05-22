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

If no provider is enabled, CodeAlta opens the Model Providers dialog automatically. Enable one provider, configure credentials, click **Test**, then **Save**.

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
