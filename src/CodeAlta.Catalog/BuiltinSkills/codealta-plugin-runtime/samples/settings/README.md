# Build watcher

Declares three settings with `GetSettings()`: a number, a choice agents may change, and a list. CodeAlta Desktop shows them on the page of the plugin in Settings > Extensions, finds them in the search of Settings and with `Ctrl+P`, and gives them to agents (`alta settings list watcher`). The values are kept in `~/.alta/config.toml` under `[plugins.settings-sample]`.

The plugin reads a value when it uses it (`Services.Settings.GetInt64("poll_minutes")`) and logs each change it is told of (`Services.Settings.Changed`). Run `/watcher_status` to see the values.
