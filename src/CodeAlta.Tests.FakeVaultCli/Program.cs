using System.Text.Json;
using System.Text.Json.Nodes;

// A fake command line of a vault for the tests of the credential providers. It plays the program it is named after (op), or
// generic commands for the command provider, from a state file beside it (fake-vault.json), and logs each call (fake-vault.log).
var folder = AppContext.BaseDirectory;
var statePath = Path.Combine(folder, "fake-vault.json");
var state = File.Exists(statePath) ? JsonNode.Parse(File.ReadAllText(statePath))!.AsObject() : new JsonObject();
File.AppendAllText(Path.Combine(folder, "fake-vault.log"), string.Join(' ', args) + Environment.NewLine);
var program = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "op").ToLowerInvariant();
if (Environment.GetEnvironmentVariable("FAKE_VAULT_REQUIRE_ENV") is { Length: > 0 } required && Environment.GetEnvironmentVariable(required) is null)
{
    Console.Error.WriteLine($"missing {required}");
    return 3;
}

return args switch
{
    ["print", var text] => Print(text),
    ["print-json", var json] => Print(json),
    ["fail", var message] => Fail(message, 2),
    ["status-ok"] => 0,
    ["status-locked"] => Fail("the vault is locked", 1),
    ["env", var name] => Print(Environment.GetEnvironmentVariable(name) ?? "(unset)"),
    // A program that never reads its standard input: a write of a large input to it blocks.
    ["sleep", var seconds] => Sleep(int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)),
    ["--version"] => Print("2.42.0"),
    _ when program == "op" => Op(args),
    _ when program == "bw" => Bw(args),
    _ when program == "bws" => Bws(args),
    _ when program == "keepassxc-cli" => KeePassXc(args),
    _ when program is "pass" or "gopass" => Pass(args, program == "gopass"),
    _ => Fail("unknown command", 64),
};

// bw: state "bw" = { status: "unauthenticated" | "locked", email, masterPassword, sessionKey, items: [the items as bw writes them],
// remoteItems: [items on the server only, until a sync], syncFails: a sync that cannot reach the server, syncSeconds: a slow sync }.
// The vault is unlocked for a call whose BW_SESSION is the session key.
int Bw(string[] arguments)
{
    var bw = state["bw"]?.AsObject() ?? [];
    var loggedIn = bw["status"]?.GetValue<string>() != "unauthenticated";
    var unlocked = loggedIn && Environment.GetEnvironmentVariable("BW_SESSION") is { Length: > 0 } session && session == bw["sessionKey"]?.GetValue<string>();
    var items = bw["items"]?.AsArray() ?? [];
    JsonNode? Item(string id) => items.FirstOrDefault(item => Same(item!["id"], id));
    switch (arguments)
    {
        case ["status"]:
            return Print(new JsonObject
            {
                ["serverUrl"] = null, ["userEmail"] = loggedIn ? bw["email"]?.GetValue<string>() : null,
                ["status"] = !loggedIn ? "unauthenticated" : unlocked ? "unlocked" : "locked",
            }.ToJsonString());
        case ["unlock", "--raw", "--passwordenv", var variable]:
            if (!loggedIn) return Fail("You are not logged in.", 1);
            if (Environment.GetEnvironmentVariable(variable) != bw["masterPassword"]?.GetValue<string>()) return Fail("Invalid master password.", 1);
            Console.Out.Write(bw["sessionKey"]!.GetValue<string>());
            return 0;
    }

    if (!loggedIn) return Fail("You are not logged in.", 1);
    if (!unlocked) return Fail("Vault is locked.", 1);
    switch (arguments)
    {
        case ["sync"]:
            if (bw["syncSeconds"]?.GetValue<int>() is { } seconds) Thread.Sleep(TimeSpan.FromSeconds(seconds));
            if (bw["syncFails"]?.GetValue<bool>() == true) return Fail("fetch failed, reason: getaddrinfo ENOTFOUND vault.bitwarden.com", 1);
            if (bw["remoteItems"]?.AsArray() is { Count: > 0 } remote)
            {
                foreach (var added in remote.ToList())
                {
                    remote.Remove(added);
                    items.Add(added);
                }

                if (items.Parent is null) bw["items"] = items;
                if (bw.Parent is null) state["bw"] = bw;
                File.WriteAllText(statePath, state.ToJsonString());
            }

            return Print("Syncing complete.");
        case ["list", "items"]:
            return Print(items.ToJsonString());
        case ["get", "item", var id]:
            return Item(id) is { } item ? Print(item.ToJsonString()) : Fail("Not found.", 1);
        case ["get", var field, var id]:
        {
            if (Item(id) is not { } entry) return Fail("Not found.", 1);
            var login = entry["login"];
            var value = field switch
            {
                "password" => login?["password"]?.GetValue<string>(),
                "username" => login?["username"]?.GetValue<string>(),
                "totp" => login?["totp"] is not null ? "123456" : null,
                "notes" => entry["notes"]?.GetValue<string>(),
                "uri" => login?["uris"]?.AsArray().FirstOrDefault()?["uri"]?.GetValue<string>(),
                _ => null,
            };
            if (value is null) return Fail(field == "totp" ? "No TOTP available for this login." : "Not found.", 1);
            Console.Out.Write(value);
            return 0;
        }

        default:
            return Fail("Invalid command", 1);
    }
}

// bws: state "bws" = { token, secrets: [{ id, key, value }] }; the token is BWS_ACCESS_TOKEN.
int Bws(string[] arguments)
{
    var bws = state["bws"]?.AsObject() ?? [];
    if (Environment.GetEnvironmentVariable("BWS_ACCESS_TOKEN") != bws["token"]?.GetValue<string>())
        return Fail("Error: \n   0: Access token is not in a valid format: Doesn't contain a decryption key", 1);
    var secrets = bws["secrets"]?.AsArray() ?? [];
    switch (arguments)
    {
        case ["project", "list", "--output", "json"]:
            return Print("[]");
        case ["secret", "list", "--output", "json"]:
            return Print(secrets.ToJsonString());
        case ["secret", "get", var id, "--output", "json"]:
            return secrets.FirstOrDefault(secret => Same(secret!["id"], id)) is { } found
                ? Print(found.ToJsonString())
                : Fail("Error: \n   0: Received error message from server: [404 Not Found] {\"message\":\"Resource not found.\"}", 1);
        default:
            return Fail("error: unrecognized subcommand", 2);
    }
}

// keepassxc-cli: state "keepassxc" = { password, keyFile (its file name), noPassword, groups: ["Work/"], entries: [{ path, attributes: {}, totp }] }.
// The password is the first line of the standard input, unless --no-password.
int KeePassXc(string[] arguments)
{
    var kp = state["keepassxc"]?.AsObject() ?? [];
    var flags = new HashSet<string>(StringComparer.Ordinal);
    string? attribute = null, keyFile = null;
    var positional = new List<string>();
    for (var index = 1; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "-a": attribute = arguments[++index]; break;
            case "-k": keyFile = arguments[++index]; break;
            case var option when option.StartsWith('-'): flags.Add(option); break;
            default: positional.Add(arguments[index]); break;
        }
    }

    if (positional.Count == 0 || !File.Exists(positional[0])) return Fail($"Failed to open database file {(positional.Count == 0 ? "" : positional[0])}: not found", 1);
    var password = flags.Contains("--no-password") ? null : Console.In.ReadLine();
    var credentialsOk = kp["noPassword"]?.GetValue<bool>() == true ? flags.Contains("--no-password") : password == kp["password"]?.GetValue<string>();
    if (kp["keyFile"]?.GetValue<string>() is { } expected && (keyFile is null || Path.GetFileName(keyFile) != expected)) credentialsOk = false;
    // keepassxc-cli 2.8 run with -q writes nothing when the database refuses the password or the key file.
    if (!credentialsOk) return flags.Contains("-q") ? 1 : Fail($"Error while reading the database {positional[0]}: Invalid credentials were provided, please try again.", 1);

    var entries = kp["entries"]?.AsArray() ?? [];
    switch (arguments[0])
    {
        case "ls":
            if (!flags.Contains("-R")) return Print(string.Join('\n', entries.Select(entry => entry!["path"]!.GetValue<string>()).Where(path => !path.Contains('/'))));
            var lines = new List<string>();
            lines.AddRange(entries.Select(entry => entry!["path"]!.GetValue<string>()));
            lines.AddRange((kp["groups"]?.AsArray() ?? []).Select(group => group!.GetValue<string>()));
            return Print(string.Join('\n', lines));
        case "show" when positional.Count == 2:
        {
            var entry = entries.FirstOrDefault(item => Same(item!["path"], positional[1].TrimStart('/')));
            if (entry is null) return Fail($"Could not find entry with path {positional[1]}.", 1);
            if (flags.Contains("-t")) return entry["totp"]?.GetValue<bool>() == true ? Print("123456") : Fail($"Entry with path {positional[1]} has no TOTP set up.", 1);
            var value = attribute is null ? null : entry["attributes"]?.AsObject().FirstOrDefault(pair => string.Equals(pair.Key, attribute, StringComparison.OrdinalIgnoreCase)).Value;
            return value is null ? Fail($"ERROR: unknown attribute {attribute}.", 1) : Print(value.GetValue<string>());
        }

        default:
            return Fail("Invalid command", 1);
    }
}

// pass and gopass: state "pass" = { entries: { "web/github": "the text of the file" }, store (the PASSWORD_STORE_DIR it requires), hang }.
int Pass(string[] arguments, bool gopass)
{
    var pass = state["pass"]?.AsObject() ?? [];
    if (pass["hang"]?.GetValue<bool>() == true)
    {
        // A pinentry that waits for somebody.
        Thread.Sleep(TimeSpan.FromMinutes(2));
        return 2;
    }

    // The key needs its passphrase: gpg asked not to start pinentry fails at once, as gpg does with --pinentry-mode error.
    if (pass["needsPassphrase"]?.GetValue<bool>() == true)
    {
        var options = Environment.GetEnvironmentVariable(gopass && Environment.GetEnvironmentVariable("GOPASS_GPG_OPTS") is { Length: > 0 } ? "GOPASS_GPG_OPTS" : "PASSWORD_STORE_GPG_OPTS") ?? string.Empty;
        if (options.Contains("--pinentry-mode error", StringComparison.Ordinal)) return Fail("gpg: public key decryption failed: No pinentry\ngpg: decryption failed: No secret key", 2);
        Thread.Sleep(TimeSpan.FromMinutes(2));
        return 2;
    }

    if (pass["store"]?.GetValue<string>() is { } store && Environment.GetEnvironmentVariable("PASSWORD_STORE_DIR") != store) return Fail("Error: the password store is not initialized.", 1);
    var entries = pass["entries"]?.AsObject() ?? [];
    string? Text(string path) => entries.FirstOrDefault(pair => pair.Key == path).Value?.GetValue<string>();
    switch (arguments)
    {
        case ["show", var path] when !gopass:
            return Text(path) is { } text ? Print(text) : Fail($"Error: {path} is not in the password store.", 1);
        case ["show", "-o", var path] when gopass:
            return Text(path) is { } secret ? Print(secret.Split('\n')[0]) : Fail($"Entry '{path}' not found. Did you mean (none)?", 11);
        case ["show", var path, var key] when gopass:
        {
            if (Text(path) is not { } content) return Fail($"Entry '{path}' not found. Did you mean (none)?", 11);
            var line = content.Split('\n').Skip(1).FirstOrDefault(item => item.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
            return line is null ? Fail($"failed to show key '{key}': key not found", 11) : Print(line[(key.Length + 1)..].Trim());
        }

        default:
            return Fail("Error: unknown command", 1);
    }
}

int Op(string[] arguments)
{
    var list = arguments.ToList();
    var account = TakeOption(list, "--account");
    if (state["serviceAccount"]?.GetValue<bool>() == true && Environment.GetEnvironmentVariable("OP_SERVICE_ACCOUNT_TOKEN") is not { Length: > 0 })
        return Fail("[ERROR] no account found for filter", 1);
    // The accounts the 1Password app gives op, without a prompt: none while its integration with the CLI is off.
    if (list is ["account", "list", ..])
        return Print(state["accounts"]?.GetValue<bool>() == false ? "[]"
            : "[{\"url\":\"my.1password.com\",\"email\":\"alice@example.com\",\"user_uuid\":\"U1\",\"account_uuid\":\"A1\",\"shorthand\":\"my\"}]");
    // What op 2.42 answers when the 1Password app gives it no account: the integration with the CLI is off, or the app is locked.
    if (state["accounts"]?.GetValue<bool>() == false && Environment.GetEnvironmentVariable("OP_SERVICE_ACCOUNT_TOKEN") is not { Length: > 0 })
        return Fail($"[ERROR] 2026/11/11 18:51:52 no account found for filter {account}", 1);
    var signedIn = state["signedIn"]?.GetValue<bool>() == true || Environment.GetEnvironmentVariable("OP_SERVICE_ACCOUNT_TOKEN") is { Length: > 0 };
    switch (list)
    {
        // With the 1Password app, op 2.42 says "not signed in" to whoami even after the app authorized the process.
        case ["whoami", ..] when state["whoamiSignedOut"]?.GetValue<bool>() == true && Environment.GetEnvironmentVariable("OP_SERVICE_ACCOUNT_TOKEN") is not { Length: > 0 }:
            return Fail("[ERROR] 2026/11/11 18:59:42 account is not signed in", 1);
        case ["whoami", ..]:
            return signedIn ? Print("{\"url\":\"my.1password.com\",\"email\":\"alice@example.com\"}") : Fail("[ERROR] account is not signed in", 1);
        case ["signin", ..]:
        case ["vault", "list", ..]:
        case ["read", ..]:
        case ["item", ..]:
            if (!signedIn)
            {
                // The prompt of the app: the state says whether the user approves it.
                if (state["approve"]?.GetValue<bool>() != true) return Fail("[ERROR] authorization prompt dismissed, please try again", 1);
                state["signedIn"] = true;
                File.WriteAllText(statePath, state.ToJsonString());
            }

            break;
    }

    var vaults = state["vaults"]?.AsArray() ?? [];
    switch (list)
    {
        case ["signin", ..]:
            return 0;
        case ["vault", "list", ..]:
            return Print(new JsonArray([.. vaults.Select(vault => (JsonNode)new JsonObject { ["id"] = vault!["id"]!.GetValue<string>(), ["name"] = vault["name"]!.GetValue<string>() })]).ToJsonString());
        case ["item", "list", ..]:
        {
            var vault = Vault(TakeOption(list, "--vault"));
            if (vault is null) return Fail("[ERROR] isn't a vault in this account", 1);
            return Print(new JsonArray([.. vault["items"]!.AsArray().Select(item => (JsonNode)new JsonObject { ["id"] = item!["id"]!.GetValue<string>(), ["title"] = item["title"]!.GetValue<string>(), ["category"] = item["category"]?.GetValue<string>() ?? "LOGIN" })]).ToJsonString());
        }
        case ["item", "get", var itemId, ..]:
        {
            var vault = Vault(TakeOption(list, "--vault"));
            var item = vault?["items"]!.AsArray().FirstOrDefault(entry => Same(entry!["id"], itemId) || Same(entry["title"], itemId));
            if (vault is null || item is null) return Fail("[ERROR] isn't an item", 1);
            var fields = new JsonArray();
            foreach (var field in item["fields"]!.AsArray())
            {
                fields.Add(new JsonObject
                {
                    ["id"] = field!["id"]!.GetValue<string>(), ["label"] = field["label"]?.GetValue<string>(), ["type"] = field["type"]?.GetValue<string>() ?? "STRING",
                    ["value"] = field["value"]?.GetValue<string>(),
                    ["reference"] = $"op://{vault["name"]}/{item["title"]}/{field["id"]}",
                });
            }

            return Print(new JsonObject { ["id"] = item["id"]!.GetValue<string>(), ["title"] = item["title"]!.GetValue<string>(), ["vault"] = new JsonObject { ["id"] = vault["id"]!.GetValue<string>(), ["name"] = vault["name"]!.GetValue<string>() }, ["fields"] = fields }.ToJsonString());
        }
        case ["read", .., var reference]:
        {
            var path = reference["op://".Length..];
            var query = path.IndexOf('?');
            var otp = query >= 0 && path[query..].Contains("attribute=otp");
            if (query >= 0) path = path[..query];
            var parts = path.Split('/');
            if (parts.Length < 3) return Fail("[ERROR] invalid secret reference", 1);
            var vault = Vault(parts[0]);
            var item = vault?["items"]!.AsArray().FirstOrDefault(entry => Same(entry!["id"], parts[1]) || Same(entry["title"], parts[1]));
            var field = item?["fields"]!.AsArray().FirstOrDefault(entry => Same(entry!["id"], parts[^1]) || Same(entry["label"], parts[^1]));
            if (field is null) return Fail($"[ERROR] could not read secret '{reference}': isn't an item or does not have a field", 1);
            Console.Out.Write(otp ? "123456" : field["value"]!.GetValue<string>());
            return 0;
        }
        default:
            return Fail("[ERROR] unknown command", 1);
    }
}

JsonNode? Vault(string? name) => (state["vaults"]?.AsArray() ?? []).FirstOrDefault(vault => Same(vault!["id"], name) || Same(vault["name"], name));

static bool Same(JsonNode? node, string? text) => node is not null && text is not null && string.Equals(node.GetValue<string>(), text, StringComparison.OrdinalIgnoreCase);

static string? TakeOption(List<string> list, string option)
{
    var at = list.IndexOf(option);
    if (at < 0 || at + 1 >= list.Count) return null;
    var value = list[at + 1];
    list.RemoveRange(at, 2);
    return value;
}

int Sleep(int seconds)
{
    // The test checks that the program was ended.
    File.WriteAllText(Path.Combine(folder, "fake-vault.pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    return 0;
}

static int Print(string text)
{
    Console.Out.WriteLine(text);
    return 0;
}

static int Fail(string message, int code)
{
    Console.Error.WriteLine(message);
    return code;
}
